module BookmarkSearch

open Fable.Core
open Fable.Core.JsInterop
open Fable.Import.Browser
open Fable.Import

open Shared
let browserp:WebExtBrowser = importDefault "chrome-promise"

open Fable.PowerPack
open Fable.PowerPack.Fetch
open Fable.PowerPack.Date

open Chessie.ErrorHandling

open System
open System.Text.RegularExpressions

let defaultstate = Idle
let mutable state = defaultstate
let SetState x =
    state <- x
    browser.runtime.sendMessage (box (x |> StateUpdate))

type [<Pojo>] SearchElem = {Url:ElemUrl; UrlStr:string; Text:string option;}

let getpercent x total =
    ((float x/float total)*100.0) |> int

let GetText url = async {
        try
            let! res = fetch url [Mode RequestMode.Cors] |> Async.AwaitPromise
            let! restxt = res.text () |> Async.AwaitPromise
            return ok restxt
        with
            | error -> return fail error
}

let HTMLToText x =
    Regex.Replace (x, @"<(.|\n)*?>", "")

let SearchOptions threshold =
    createObj [
        "expand" ==> true
    ]

type [<Pojo>] ElasticSearchRes = {ref:int; score:float}
type ElasticIndex = {addDoc: obj -> unit; search: string -> obj -> ElasticSearchRes array}
type ElasticThis = {addField:string -> unit; setRef:string -> unit; saveDocument: bool -> unit; ``use``:obj -> unit}
type Elasticlunr = (ElasticThis -> unit) -> ElasticIndex
let elasticlunr:obj = importDefault "elasticlunr"
let stopwordfilter:obj -> unit = importDefault "./lunr-languages/stopwordfilter.js"
elasticlunr |> stopwordfilter

let importLunr x =
    x(elasticlunr) |> ignore

let supportedlanguages = ["en"; "ru"]

importDefault "./lunr-languages/min/lunr.stemmer.support.min.js" |> importLunr
let languages = browserp.i18n.getAcceptLanguages () |> Promise.bind (Array.filter (fun x -> List.exists (fun y -> y=x) supportedlanguages) >> Promise.lift)
                |> Promise.bind (Array.map
                    (fun x ->
                        match x with
                            | "ru" -> importDefault "./lunr-languages/min/lunr.ru.min.js" |> importLunr
                            | _ -> ()
                        x) >> Promise.lift)

importDefault "./lunr-languages/min/lunr.multi.min.js" |> importLunr

[<Emit("$0.multiLanguage(...$1)")>]
let usemultilanguage (x:obj) (y:string array) : obj = jsNative

let LoopArr arr dowhat = async {
    let len = Array.length arr
    let mutable currentindex = 0
    let timesep = 10
    let interval =
        window.setInterval ((fun _ ->
                                if currentindex < len then
                                    let x = Array.item currentindex arr
                                    dowhat currentindex len x
                                    currentindex<-currentindex+1
                                ),timesep)
    do! Async.Sleep ((len+1)*timesep)
    window.clearInterval(interval)
}

let SearchElemArray (arr:SearchElem array) keys threshold query =
    async {
        try
            let opts = (SearchOptions threshold)

            let elasticlunrfunc:Elasticlunr = !!elasticlunr
            let! languages = languages |> Async.AwaitPromise

            let i = elasticlunrfunc (fun x ->
                if languages.Length > 0 then
                    x.``use`` (usemultilanguage elasticlunr languages)

                keys |> Array.iter (fun y -> x.addField(y))
                x.saveDocument false
            )

            let withi = arr |> Array.mapi (fun i -> box >> (fun x -> x?id <- i; x))
            do! LoopArr withi (fun id total x -> Indexing (getpercent id total) |> Searching |> SetState; i.addDoc(x))

            Searching SearchStage.Searching |> SetState
            let x = i.search query opts

            return ok (x |> Array.filter (fun {score=x} -> x >= threshold) |> Array.map (fun {ref=x} -> Array.item x arr |> function | {Url=x} -> x) |> Array.distinct)
        with | error -> return fail error.Message
    } |> AR

let ValidateSearch x =
    if x |> String.length = 0 then fail "Cannot use a string with a length of 0" else ok x

let ValidateNumber x =
    try
        x |> float |> ok
    with | error -> fail error.Message

let GetBookmarks () = async {
    let! tree = browserp.bookmarks.search (createObj []) |> Async.AwaitPromise
    return tree |> Array.choose (function {url=x} -> x)
}

let GetHistory (days:float) maxres = async {
    let options =
        createObj [
            "text" ==> "";
            "startTime" ==> ((864000.0*days |> int)+(DateTimeOffset.Now.ToUnixTimeSeconds() |> int));
            "maxResults" ==> maxres
        ]
    let! history = browserp.history.search options |> Async.AwaitPromise
    return history |> Array.choose (fun {url=url} -> url)
}

let Equals x y = x=y

let SearchRes {ToSearch=tosearch;Accuracy=accuracy;HistoryDays=historydays;HistoryResults=historyresults;HistoryBookmarks=historybookmarks;SearchMethod=searchmethod} = asyncTrial {
    Searching RetrievingUrls |> SetState

    let! tosearch = tosearch |> ValidateSearch
    let accuracy = accuracy |> float

    let dohistory = [0;1] |> List.exists (Equals historybookmarks)
    let dobookmarks = [0;2] |> List.exists (Equals historybookmarks)

    let dohtml = [0;2] |> List.exists (Equals searchmethod)
    let dourl = [0;1] |> List.exists (Equals searchmethod)

    let addiftrue y = function
        | true -> [|y|] | _ -> [||]

    let keys = Array.concat [dohtml |> addiftrue "Text"; dourl |> addiftrue "UrlStr"]

    let! bookmarks = async {
        if dobookmarks then
            let! bookmarks = GetBookmarks ()
            return bookmarks |> Array.map Bookmark
        else return [||]
    }

    let! history = asyncTrial {
        if dohistory then
            let! historydays = historydays |> ValidateNumber
            let! historyres = historyresults |> ValidateNumber
            let! history = GetHistory historydays historyres
            return history |> Array.map History
        else
            return [||]
    }

    let urls = Array.append bookmarks history

    let format x =
        Regex.Replace (x, @"\n" ," ") |> (fun x -> Regex.Replace (x,@"[ ](?= )","")) |> (fun x -> x.Substring (0,80000))

    let! elems =
        match dohtml with
            | true -> asyncTrial {
                    let len = urls |> Array.length
                    GettingText |> Searching |> SetState
                    let! response = urls |> Array.map (EUrlStr >> GetText) |> Async.Parallel
                    let text = response |> Array.map (function | Pass x -> Some (x |> HTMLToText |> format) | _ -> None)

                    return Array.zip urls text |> Array.map (fun (url, txt) -> {Url=url;UrlStr=EUrlStr url;Text=txt})
                }
            | false -> asyncTrial {return urls |> Array.map (fun x -> {Url=x;UrlStr=EUrlStr x;Text=None})}

    let! res = SearchElemArray elems keys accuracy tosearch
    return res
}

let Search data = async {
    let! res = SearchRes data |> Async.ofAsyncResult
    res |> Finished |> SetState
}

let HandleMessage x =
    match x with
        | StartSearch y ->
            y |> Search |> Async.StartAsPromise |> ignore
        | GetState ->
            SetState state
        | _ -> ()

let f = Func<obj,unit>(fun x -> HandleMessage (unbox<Message> x))
browser.runtime.onMessage.addListener f
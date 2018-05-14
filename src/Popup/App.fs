module Popup

open Fable.Core
open Fable.Core.JsInterop
open Fable.Import.Browser
open Fable.Import

open Fable.PowerPack
open Fable.PowerPack.Fetch
open Fable.PowerPack.Date

open System
open System.Text.RegularExpressions

open Shared

open Chessie.ErrorHandling

importAll "./App.sass"

let getvalue (x:obj) =
    (x :?> HTMLInputElement).value

let getselectvalue (x:obj) =
    (x :?> HTMLSelectElement).value |> int

let bgpage = browser.runtime.getBackgroundPage () |> Async.AwaitPromise

window.onload <- (fun x ->
    let searchbutton = (document.querySelector "#search") :?> HTMLButtonElement

    GetState |> box |> browser.runtime.sendMessage

    searchbutton.onclick <- (fun _ ->
        let tosearch = document.querySelector "#tosearch" |> getvalue
        let accuracy = document.querySelector "#accuracy" |> getvalue
        let hd = document.querySelector "#historydays" |> getvalue
        let hr = document.querySelector "#historyresults" |> getvalue
        let hb = document.querySelector "#historybookmarks" |> getselectvalue
        let sm = document.querySelector "#searchmethod" |> getselectvalue

        let d = {ToSearch=tosearch;Accuracy=accuracy;HistoryDays=hd;HistoryResults=hr;HistoryBookmarks=hb;SearchMethod=sm}
        d |> StartSearch |> box |> browser.runtime.sendMessage)
)

let SetStatus x =
    (document.querySelector "#status").innerHTML <- x

let HandleState x =
    let searchbutton = (document.querySelector "#search") :?> HTMLButtonElement

    match x with
        | Searching -> searchbutton.disabled <- true | _ -> searchbutton.disabled <- false

    match x with
        | Searching ->
            SetStatus "Searching..."
        | Finished (Pass x) when Array.length x > 0 ->
            SetStatus (x |> Array.map (fun x -> sprintf "<p><a href=\"%s\" >%s</a></p>" x x) |> Array.reduce (+))
        | Finished (Pass x) ->
            SetStatus "No results found!"
        | Finished (Fail (x::_)) ->
            SetStatus x
        | _ -> ()

let HandleMessage = function
    | StateUpdate x -> HandleState x
    | _ -> ()

let f = Func<obj,unit>(fun x -> HandleMessage (unbox<Message> x))
browser.runtime.onMessage.addListener f
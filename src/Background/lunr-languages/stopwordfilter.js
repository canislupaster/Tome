module.exports = function (x) {x.generateStopWordFilter = function(stopWords){var words=stopWords.reduce(function(memo,stopWord){memo[stopWord]=stopWord;return memo},{});return function(token){if(token&&words[token.toString()]!==token.toString()){return token}}}}
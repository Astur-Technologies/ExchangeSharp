using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ExchangeSharp.API.Exchanges.FTX
{
	public sealed partial class ExchangeFTXAPI : ExchangeAPI
	{
		public override string BaseUrl { get; set; } = "https://ftx.com/api";
		public override string BaseUrlWebSocket { get; set; } = "wss://ftx.com/ws/";

		public ExchangeFTXAPI()
		{
			NonceStyle = NonceStyle.UnixMillisecondsString;
			MarketSymbolSeparator = "/";
			//WebSocketOrderBookType = WebSocketOrderBookType.
		}

		protected async override Task<Dictionary<string, decimal>> OnGetAmountsAsync()
		{
			var balances = new Dictionary<string, decimal>();

			JToken result = await MakeJsonRequestAsync<JToken>("/wallet/balances", null, await GetNoncePayloadAsync());

			foreach (JObject obj in result)
			{
				decimal amount = obj["total"].ConvertInvariant<decimal>();

				balances[obj["coin"].ToStringInvariant()] = amount;
			}

			return balances;
		}

		protected async override Task OnGetHistoricalTradesAsync(Func<IEnumerable<ExchangeTrade>, bool> callback, string marketSymbol, DateTime? startDate = null, DateTime? endDate = null, int? limit = null)
		{
			string baseUrl = $"/markets/{marketSymbol}/trades?";

			if (startDate != null)
			{
				baseUrl += $"start_time={startDate?.UnixTimestampFromDateTimeMilliseconds()}";
			}

			if (endDate != null)
			{
				baseUrl += $"start_time={endDate?.UnixTimestampFromDateTimeMilliseconds()}";
			}

			List<ExchangeTrade> trades = new List<ExchangeTrade>();

			while (true)
			{
				JToken result = await MakeJsonRequestAsync<JToken>(baseUrl);

				foreach (JToken trade in result.Children())
				{
					trades.Add(trade.ParseTrade("size", "price", "side", "time", TimestampType.Iso8601, "id", "buy"));
				}

				if (!callback(trades))
				{
					break;
				}

				Task.Delay(1000).Wait();
			}
		}

		protected async override Task<IEnumerable<string>> OnGetMarketSymbolsAsync(bool isWebSocket = false)
		{
			JToken result = await MakeJsonRequestAsync<JToken>("/markets");

			var names = result.Children().Select(x => x["name"].ToStringInvariant()).Where(x => Regex.Match(x, @"[\w\d]*\/[[\w\d]]*").Success).ToList();

			names.Sort();

			return names;
		}

		protected async internal override Task<IEnumerable<ExchangeMarket>> OnGetMarketSymbolsMetadataAsync()
		{
			//{
			//	"name": "BTC-0628",
			//	"baseCurrency": null,
			//	"quoteCurrency": null,
			//	"quoteVolume24h": 28914.76,
			//	"change1h": 0.012,
			//	"change24h": 0.0299,
			//	"changeBod": 0.0156,
			//	"highLeverageFeeExempt": false,
			//	"minProvideSize": 0.001,
			//	"type": "future",
			//	"underlying": "BTC",
			//	"enabled": true,
			//	"ask": 3949.25,
			//	"bid": 3949,
			//	"last": 10579.52,
			//	"postOnly": false,
			//	"price": 10579.52,
			//	"priceIncrement": 0.25,
			//	"sizeIncrement": 0.0001,
			//	"restricted": false,
			//	"volumeUsd24h": 28914.76
			//}

			var markets = new List<ExchangeMarket>();

			JToken result = await MakeJsonRequestAsync<JToken>("/markets");

			foreach (JToken token in result.Children())
			{
				var symbol = token["name"].ToStringInvariant();

				if (!Regex.Match(symbol, @"[\w\d]*\/[[\w\d]]*").Success)
				{
					continue;
				}

				var market = new ExchangeMarket()
				{
					MarketSymbol = symbol,
					BaseCurrency = token["baseCurrency"].ToStringInvariant(),
					QuoteCurrency = token["quoteCurrency"].ToStringInvariant(),
					PriceStepSize = token["priceIncrement"].ConvertInvariant<decimal>(),
					QuantityStepSize = token["sizeIncrement"].ConvertInvariant<decimal>(),
					MinTradeSize = token["minProvideSize"].ConvertInvariant<decimal>(),
					IsActive = token["enabled"].ConvertInvariant<bool>(),
				};

				markets.Add(market);
			}

			return markets;
		}

		protected async override Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string marketSymbol = null)
		{
			var markets = new List<ExchangeOrderResult>();

			JToken result = await MakeJsonRequestAsync<JToken>($"/orders?market={marketSymbol}");

			foreach (JToken token in result.Children())
			{
				//var symbol = token["name"].ToStringInvariant();

				//if (!Regex.Match(symbol, @"[\w\d]*\/[[\w\d]]*").Success)
				//{
				//	continue;
				//}

				markets.Add(new ExchangeOrderResult()
				{
					MarketSymbol = token["market"].ToStringInvariant(),
					Price = token["price"].ConvertInvariant<decimal>(),
					AveragePrice = token["avgFillPrice"].ConvertInvariant<decimal>(),
					OrderDate = token["createdAt"].ConvertInvariant<DateTime>(),
					IsBuy = token["side"].ToStringInvariant().Equals("buy"),
					OrderId = token["id"].ToStringInvariant(),
					Amount = token["size"].ConvertInvariant<decimal>(),
					AmountFilled = token["filledSize"].ConvertInvariant<decimal>(), // ?
					ClientOrderId = token["clientId"].ToStringInvariant()
				});
			}

			return markets;
		}

		protected async override Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string marketSymbol = null)
		{
			// https://docs.ftx.com/#get-order-status

			JToken result = await MakeJsonRequestAsync<JToken>($"/orders?{orderId}");

			var resp = result.First();

			return new ExchangeOrderResult()
			{
				OrderId = resp["id"].ToStringInvariant(),
				OrderDate = resp["createdAt"].ConvertInvariant<DateTime>(),
				Result = resp["id"].ToStringLowerInvariant().ToExchangeAPIOrderResult()
			};
		}

		protected override async Task<IWebSocket> OnGetTickersWebSocketAsync(Action<IReadOnlyCollection<KeyValuePair<string, ExchangeTicker>>> tickers, params string[] marketSymbols)
		{
			if (marketSymbols == null || marketSymbols.Length == 0)
			{
				marketSymbols = (await GetMarketSymbolsAsync(true)).ToArray();
			}
			return await ConnectPublicWebSocketAsync(null, messageCallback: async (_socket, msg) =>
			{
				JToken parsedMsg = JToken.Parse(msg.ToStringFromUTF8());

				if (parsedMsg["channel"].ToStringInvariant().Equals("ticker"))
				{
					JToken data = parsedMsg["data"];

					var exchangeTicker = await this.ParseTickerAsync(data, parsedMsg["market"].ToStringInvariant(), "ask", "bid", "last", null, null, "time", TimestampType.UnixSecondsDouble);

					var kv = new KeyValuePair<string, ExchangeTicker>(exchangeTicker.MarketSymbol, exchangeTicker);

					tickers(new List<KeyValuePair<string, ExchangeTicker>> { kv });
				}
			}, connectCallback: async (_socket) =>
			{
				List<string> marketSymbolList = marketSymbols.ToList();

				//{'op': 'subscribe', 'channel': 'trades', 'market': 'BTC-PERP'}

				for (int i = 0; i < marketSymbolList.Count; i++)
				{
					await _socket.SendMessageAsync(new
					{
						op = "subscribe",
						market = marketSymbolList[i],
						channel = "ticker"
					});
				}				
			});
		}

		protected override async Task ProcessRequestAsync(IHttpWebRequest request, Dictionary<string, object> payload)
		{
			if (CanMakeAuthenticatedRequest(payload))
			{
				string timestamp = payload["nonce"].ToStringInvariant();

				string form = CryptoUtility.GetJsonForPayload(payload);

				//Create the signature payload
				string toHash = $"{timestamp}{request.Method.ToUpperInvariant()}{request.RequestUri.PathAndQuery}";

				if (request.Method == "POST")
				{
					toHash += form;

					await CryptoUtility.WriteToRequestAsync(request, form);
				}

				byte[] secret = CryptoUtility.ToUnsecureBytesUTF8(PrivateApiKey);

				string signatureHexString = CryptoUtility.SHA256Sign(toHash, secret);

				request.AddHeader("FTX-KEY", PublicApiKey.ToUnsecureString());
				request.AddHeader("FTX-SIGN", signatureHexString);
				request.AddHeader("FTX-TS", timestamp);
			}
		}
	}
}

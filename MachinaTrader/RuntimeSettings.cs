using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MachinaTrader.Globals;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Mynt.Core.Exchanges;
using Mynt.Core.Notifications;
using Mynt.Data.LiteDB;
using Mynt.Data.MongoDB;
using MachinaTrader.Helpers;
using MachinaTrader.Hubs;
using Quartz;
using Quartz.Impl;
using MachinaTrader.Globals.Structure.Enums;
using MachinaTrader.Globals.Structure.Interfaces;
using MachinaTrader.Globals.Structure.Models;
using ExchangeSharp;

namespace MachinaTrader
{
    public static class Runtime
    {
        public static IHubContext<HubTraders> GlobalHubTraders;
        public static IHubContext<HubStatistics> GlobalHubStatistics;
        public static IHubContext<HubBacktest> GlobalHubBacktest;
        public static IHubContext<HubAccounts> GlobalHubAccounts;
        public static TelegramNotificationOptions GlobalTelegramNotificationOptions { get; set; }
        public static List<INotificationManager> NotificationManagers;
        public static OrderBehavior GlobalOrderBehavior;
        public static ConcurrentDictionary<string, Ticker> WebSocketTickers = new ConcurrentDictionary<string, Ticker>();
        
        public static List<string> GlobalCurrencys = new List<string>();
        public static List<string> ExchangeCurrencys = new List<string>();
    }

    /// <summary>
    /// Global Settings
    /// </summary>
    public class RuntimeSettings
    {
        public async static void Init()
        {
            Runtime.GlobalOrderBehavior = OrderBehavior.CheckMarket;

            Runtime.NotificationManagers = new List<INotificationManager>()
            {
                new SignalrNotificationManager(),
                new TelegramNotificationManager(Runtime.GlobalTelegramNotificationOptions)
            };

            if (Global.Configuration.SystemOptions.Database == "MongoDB")
            {
                Global.Logger.Information("Database set to MongoDB");
                MongoDbOptions databaseOptions = new MongoDbOptions();
                Global.DataStore = new MongoDbDataStore(databaseOptions);
                MongoDbOptions backtestDatabaseOptions = new MongoDbOptions();
                Global.DataStoreBacktest = new MongoDbDataStoreBacktest(backtestDatabaseOptions);
            }
            else
            {
                Global.Logger.Information("Database set to LiteDB");
                LiteDbOptions databaseOptions = new LiteDbOptions { LiteDbName = Global.DataPath + "/MachinaTrader.db" };
                Global.DataStore = new LiteDbDataStore(databaseOptions);
                LiteDbOptions backtestDatabaseOptions = new LiteDbOptions { LiteDbName = Global.DataPath + "/MachinaTrader.db" };
                Global.DataStoreBacktest = new LiteDbDataStoreBacktest(backtestDatabaseOptions);
            }

            // Global Hubs
            Runtime.GlobalHubTraders = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubTraders>>();
            Runtime.GlobalHubStatistics = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubStatistics>>();
            Runtime.GlobalHubBacktest = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubBacktest>>();
            Runtime.GlobalHubAccounts = Global.ServiceScope.ServiceProvider.GetService<IHubContext<HubAccounts>>();

            //Run Cron
            IScheduler scheduler = Global.QuartzTimer;

            IJobDetail buyTimerJob = JobBuilder.Create<Timers.BuyTimer>()
                .WithIdentity("buyTimerJobTrigger", "buyTimerJob")
                .Build();

            ITrigger buyTimerJobTrigger = TriggerBuilder.Create()
                .WithIdentity("buyTimerJobTrigger", "buyTimerJob")
                .WithCronSchedule(Global.Configuration.TradeOptions.BuyTimer)
                .UsingJobData("force", false)
                .Build();

            await scheduler.ScheduleJob(buyTimerJob, buyTimerJobTrigger);

            IJobDetail sellTimerJob = JobBuilder.Create<Timers.SellTimer>()
                .WithIdentity("sellTimerJobTrigger", "sellTimerJob")
                .Build();

            ITrigger sellTimerJobTrigger = TriggerBuilder.Create()
                .WithIdentity("sellTimerJobTrigger", "sellTimerJob")
                .WithCronSchedule(Global.Configuration.TradeOptions.SellTimer)
                .UsingJobData("force", false)
                .Build();

            await scheduler.ScheduleJob(sellTimerJob, sellTimerJobTrigger);

            await scheduler.Start();
            Global.Logger.Information($"Buy Cron will run at: {buyTimerJobTrigger.GetNextFireTimeUtc() ?? DateTime.MinValue:r}");
            Global.Logger.Information($"Sell Cron will run at: {sellTimerJobTrigger.GetNextFireTimeUtc() ?? DateTime.MinValue:r}");
        }

        public static void LoadSettings()
        {
            var exchangeOption = Global.Configuration.ExchangeOptions.FirstOrDefault();
            switch (exchangeOption.Exchange)
            {
                case Exchange.GdaxSimulation:
                    exchangeOption.Exchange = Exchange.Gdax;
                    Global.ExchangeApi = new BaseExchange(exchangeOption, new SimulationExchanges.ExchangeSimulationApi(new ExchangeGdaxAPI()));
                    exchangeOption.IsSimulation = true;
                    break;
                case Exchange.BinanceSimulation:
                    exchangeOption.Exchange = Exchange.Binance;
                    Global.ExchangeApi = new BaseExchange(exchangeOption, new SimulationExchanges.ExchangeSimulationApi(new ExchangeBinanceAPI()));
                    exchangeOption.IsSimulation = true;
                    break;
                default:
                    Global.ExchangeApi = new BaseExchange(exchangeOption);
                    exchangeOption.IsSimulation = false;
                    break;
            }

            //Websocket Test
            var fullApi = Global.ExchangeApi.GetFullApi().Result;

            //Create Exchange Currencies as List
            foreach (var currency in Global.Configuration.TradeOptions.AlwaysTradeList)
            {
                Runtime.GlobalCurrencys.Add(Global.Configuration.TradeOptions.QuoteCurrency + "-" + currency);
            }

            foreach (var currency in Runtime.GlobalCurrencys)
            {
                Runtime.ExchangeCurrencys.Add(fullApi.GlobalSymbolToExchangeSymbol(currency));
            }

            if (!exchangeOption.IsSimulation)
                fullApi.GetTickersWebSocket(OnWebsocketTickersUpdated);

            // Telegram Notifications
            Runtime.GlobalTelegramNotificationOptions = Global.Configuration.TelegramOptions;
        }

        public static void OnWebsocketTickersUpdated(IReadOnlyCollection<KeyValuePair<string, ExchangeSharp.ExchangeTicker>> updatedTickers)
        {
            foreach (var update in updatedTickers)
            {
                if (Runtime.ExchangeCurrencys.Contains(update.Key))
                {
                    if (Runtime.WebSocketTickers.TryGetValue(update.Key, out Ticker ticker))
                    {
                        ticker.Ask = update.Value.Ask;
                        ticker.Bid = update.Value.Bid;
                        ticker.Last = update.Value.Last;
                    }
                    else
                    {
                        Runtime.WebSocketTickers.TryAdd(update.Key, new Ticker
                        {
                            Ask = update.Value.Ask,
                            Bid = update.Value.Bid,
                            Last = update.Value.Last
                        });
                    }
                }
            }
        }
    }
}

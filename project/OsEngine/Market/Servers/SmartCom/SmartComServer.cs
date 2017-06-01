﻿/*
 *Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.Market.Servers.Entity;
using SmartCOM3Lib;

namespace OsEngine.Market.Servers.SmartCom
{
    /// <summary>
    /// класс - сервер для подключения к СмартКом
    /// </summary>
   public class SmartComServer: IServer
    {

       /// <summary>
       /// конструктор
       /// </summary>
       public SmartComServer()
       {
           ServerPort ="8443";
           ServerAdress = "mxdemo.ittrade.ru";
           ServerStatus = ServerConnectStatus.Disconnect;
           ServerType = ServerType.SmartCom;

           Load();

           _ordersToExecute = new ConcurrentQueue<Order>();
           _ordersToCansel = new ConcurrentQueue<Order>();

           Thread ordersExecutor = new Thread(ExecutorOrdersThreadArea);
           ordersExecutor.CurrentCulture = new CultureInfo("ru-RU");
           ordersExecutor.IsBackground = true;
           ordersExecutor.Start();

           _logMaster = new Log("SmartComServer");
           _logMaster.Listen(this);

           _serverStatusNead = ServerConnectStatus.Disconnect;

           _threadPrime = new Thread(PrimeThreadArea);
           _threadPrime.CurrentCulture = new CultureInfo("ru-RU");
           _threadPrime.IsBackground = true;
           _threadPrime.Start();

           Thread threadDataSender= new Thread(SenderThreadArea);
           threadDataSender.CurrentCulture = new CultureInfo("ru-RU");
           threadDataSender.IsBackground = true;
           threadDataSender.Start();

           _ordersToSend = new ConcurrentQueue<Order>();
           _tradesToSend = new ConcurrentQueue<List<Trade>>();
           _portfolioToSend = new ConcurrentQueue<List<Portfolio>>();
           _securitiesToSend = new ConcurrentQueue<List<Security>>();
           _myTradesToSend = new ConcurrentQueue<MyTrade>();
           _newServerTime = new ConcurrentQueue<DateTime>();
           _candleSeriesToSend = new ConcurrentQueue<CandleSeries>();
           _marketDepthsToSend = new ConcurrentQueue<MarketDepth>();
           _bidAskToSend = new ConcurrentQueue<BidAskSender>();
       }

       /// <summary>
       /// взять тип сервера
       /// </summary>
       /// <returns></returns>
       public ServerType ServerType { get; set; }

       /// <summary>
       /// показать окно настроект
       /// </summary>
       public void ShowDialog()
       {
           SmartComServerUi ui = new SmartComServerUi(this,_logMaster);
           ui.ShowDialog();
       }

       /// <summary>
       /// порт по которому нужно соединяться с сервером
       /// </summary>
       public string ServerPort;

       /// <summary>
       /// адрес сервера по которому нужно соединяться с сервером
       /// </summary>
       public string ServerAdress;

       /// <summary>
       /// логин пользователя для доступа к СмартКом
       /// </summary>
       public string UserLogin;

       /// <summary>
       /// пароль пользователя для доступа к СмартКом
       /// </summary>
       public string UserPassword;

       /// <summary>
       /// загрузить настройки сервера из файла
       /// </summary>
       public void Load()
       {
           if (!File.Exists(@"Engine\" + @"SmartServer.txt"))
           {
               return;
           }

           try
           {
               using (StreamReader reader = new StreamReader(@"Engine\" + @"SmartServer.txt"))
               {
                   ServerPort = reader.ReadLine();
                   ServerAdress = reader.ReadLine();
                   UserLogin = reader.ReadLine();
                   UserPassword = reader.ReadLine();

                   reader.Close();
               }
           }
           catch (Exception)
           {
               // ignored
           }
       }

       /// <summary>
       /// сохранить настройки сервера в файл
       /// </summary>
       public void Save()
       {
           try
           {
               using (StreamWriter writer = new StreamWriter(@"Engine\" + @"SmartServer.txt", false))
               {
                   writer.WriteLine(ServerPort);
                   writer.WriteLine(ServerAdress);
                   writer.WriteLine(UserLogin);
                   writer.WriteLine(UserPassword);

                   writer.Close();
               }
           }
           catch (Exception)
           {
               // ignored
           }
       }

// статус сервера

       private ServerConnectStatus _serverConnectStatus;

       /// <summary>
       /// статус сервера
       /// </summary>
       public ServerConnectStatus ServerStatus
       {
           get { return _serverConnectStatus; }
           private set
           {
               if (value != _serverConnectStatus)
               {
                   _serverConnectStatus = value;
                   SendLogMessage(_serverConnectStatus + " Изменилось состояние соединения",LogMessageType.Connect);
                   if (ConnectStatusChangeEvent != null)
                   {
                       ConnectStatusChangeEvent(_serverConnectStatus.ToString());
                   }
               }
           }
       }

       /// <summary>
       /// вызывается когда статус соединения изменяется
       /// </summary>
       public event Action<string> ConnectStatusChangeEvent;

// подключение / отключение

       /// <summary>
       /// запустить сервер СмартКом
       /// </summary>
       public void StartServer()
       {
           _serverStatusNead = ServerConnectStatus.Connect;
       }

       /// <summary>
       /// остановить сервер СмартКом
       /// </summary>
       public void StopServer()
       {
           _serverStatusNead = ServerConnectStatus.Disconnect;
       }

       /// <summary>
       /// нужный статус сервера. Нужен потоку который следит за соединением
       /// В зависимости от этого поля управляет соединением
       /// </summary>
       private ServerConnectStatus _serverStatusNead;

       /// <summary>
       /// пришло оповещение от СмартКом, что соединение разорвано
       /// </summary>
       void SmartServer_Disconnected(string reason)
       {
           ServerStatus = ServerConnectStatus.Disconnect;
           SendLogMessage("Причина разрыва соединения " + reason, LogMessageType.System);
       }

       /// <summary>
       /// пришло оповещение от СмартКом, что соединение установлено
       /// </summary>
       void SmartServer_Connected() 
       {
           ServerStatus = ServerConnectStatus.Connect;
       }

// работа основного потока !!!!!!

       /// <summary>
       /// основной поток, следящий за подключением, загрузкой портфелей и бумаг, пересылкой данных на верх
       /// </summary>
       private Thread _threadPrime;

       /// <summary>
       /// место в котором контролируется соединение.
       /// опрашиваются потоки данных
       /// </summary>
       [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
       private void PrimeThreadArea() 
       {
           while (true)
           {
               Thread.Sleep(1000);
               lock (_smartComServerLocker)
               {
                   try
                   {
                       if (SmartServer == null)
                       {
                           SendLogMessage("Создаём коннектор СмартКом", LogMessageType.System);
                           CreateNewServerSmartCom();
                           continue;
                       }

                       bool smarStateIsActiv = SmartServer.IsConnected();

                       if (smarStateIsActiv == false && _serverStatusNead == ServerConnectStatus.Connect)
                       {
                           SendLogMessage("Запущена процедура активации подключения", LogMessageType.System);
                           Connect();
                           continue;
                       }

                       if (smarStateIsActiv && _serverStatusNead == ServerConnectStatus.Disconnect)
                       {
                           SendLogMessage("Запущена процедура отключения подключения", LogMessageType.System);
                           Disconnect();
                           continue;
                       }

                       if (smarStateIsActiv == false)
                       {
                           continue;
                       }

                       if (_candleManager == null)
                       {
                           SendLogMessage("Создаём менеджер свечей", LogMessageType.System);
                           StartCandleManager();
                           continue;
                       }

                       if (_getPortfoliosAndSecurities == false)
                       {
                           SendLogMessage("Скачиваем бумаги и портфели", LogMessageType.System);
                           GetSecuritiesAndPortfolio();
                           _getPortfoliosAndSecurities = true;
                           continue;
                       }

                       if (Portfolios == null || Securities == null)
                       {
                           _getPortfoliosAndSecurities = false;
                           continue;
                       }

                       if (_startListeningPortfolios == false)
                       {
                           if (_portfolios != null)
                           {
                               SendLogMessage("Подписываемся на обновления портфелей. Берём активные ордера", LogMessageType.System);
                               StartListeningPortfolios();
                               _startListeningPortfolios = true;
                           }
                       }
                   }
                   catch (Exception error)
                   {
                       SendLogMessage("КРИТИЧЕСКАЯ ОШИБКА. Реконнект", LogMessageType.Error);
                       SendLogMessage(error.ToString(), LogMessageType.Error);
                       ServerStatus = ServerConnectStatus.Disconnect;
                       Dispose(); // очищаем данные о предыдущем коннекторе

                       Thread.Sleep(5000);
                       // переподключаемся
                       _threadPrime = new Thread(PrimeThreadArea);
                       _threadPrime.CurrentCulture = new CultureInfo("ru-RU");
                       _threadPrime.IsBackground = true;
                       _threadPrime.Start();

                       if (NeadToReconnectEvent != null)
                       {
                           NeadToReconnectEvent();
                       }

                       return;
                   }
               }
           }
       }

       /// <summary>
       /// время последнего старта сервера
       /// </summary>
       private DateTime _lastStartServerTime = DateTime.MinValue; 

       /// <summary>
       /// включена ли прослушка портфелей
       /// </summary>
       private bool _startListeningPortfolios;

       /// <summary>
       /// скачаны ли портфели и бумаги
       /// </summary>
       private bool _getPortfoliosAndSecurities;

       /// <summary>
       /// создать новое подключение
       /// </summary>
       private void CreateNewServerSmartCom() 
       {
           if (SmartServer == null)
           {
               SmartServer = new StServerClass(); // Создать и назначить обработчики событий
               SmartServer.Connected += SmartServer_Connected;
               SmartServer.Disconnected += SmartServer_Disconnected;
               SmartServer.AddSymbol += SmartServer_AddSymbol;
               SmartServer.AddPortfolio += SmartServer_AddPortfolio;
               SmartServer.SetPortfolio += SmartServer_SetPortfolio;
               SmartServer.UpdatePosition += SmartServer_UpdatePosition;
               SmartServer.ConfigureClient("logLevel=0;maxWorkerThreads=10");
               SmartServer.AddTick += SmartServer_AddTick;
               SmartServer.AddTrade += SmartServer_AddTrade;
               SmartServer.AddBar += SmartServer_AddBar;
               SmartServer.UpdateBidAsk += SmartServer_UpdateBidAsk;

               SmartServer.UpdateOrder += SmartServer_UpdateOrder;
               SmartServer.OrderFailed += SmartServer_OrderFailed;
               SmartServer.OrderSucceeded += SmartServer_OrderSucceeded;
               SmartServer.OrderCancelFailed += SmartServer_OrderCancelFailed;
               SmartServer.OrderCancelSucceeded += SmartServer_OrderCancelSucceeded;
           }
       }

       /// <summary>
       /// начать процесс подключения
       /// </summary>
       private void Connect()
       {
           string ip = ServerAdress;
           ushort port = Convert.ToUInt16(ServerPort);
           string username = UserLogin;
           string userpassword = UserPassword;

           SmartServer.connect(ip, port, username, userpassword);
           _lastStartServerTime = DateTime.Now;
           Thread.Sleep(10000);
       }

       /// <summary>
       /// приостановить подключение
       /// </summary>
       private void Disconnect()
       {
           SmartServer.disconnect();
           Thread.Sleep(5000);
       }

       /// <summary>
       /// запускает скачиватель свечек
       /// </summary>
       private void StartCandleManager()
       {
           if (_candleManager == null)
           {
               _candleManager = new CandleManager(this);
               _candleManager.CandleUpdateEvent += _candleManager_CandleUpdateEvent;
               _candleManager.LogMessageEvent += SendLogMessage;
           }
       }

       /// <summary>
       /// включает загрузку инструментов и портфелей
       /// </summary>
       private void GetSecuritiesAndPortfolio()
       {
           SmartServer.GetPrortfolioList();
           Thread.Sleep(5000);
           SmartServer.GetSymbols();
       }

       /// <summary>
       /// включает прослушивание портфелей
       /// </summary>
       private void StartListeningPortfolios()
       {
           if (_portfolios != null)
           {
               for (int i = 0; i < _portfolios.Count; i++)
               {
                   SmartServer.ListenPortfolio(_portfolios[i].Number);
                   SmartServer.GetMyOrders(1, _portfolios[i].Number); 
               }
           }
       }

       /// <summary>
       /// привести программу к моменту запуска. Очистить все объекты участвующие в подключении к серверу
       /// </summary>
       [System.Runtime.ExceptionServices.HandleProcessCorruptedStateExceptionsAttribute]
       private void Dispose()
       {
           try
           {
               if (SmartServer != null && SmartServer.IsConnected())
               {
                   SmartServer.disconnect();
               }
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }

           if (SmartServer != null)
           {
               SmartServer.Connected -= SmartServer_Connected;
               SmartServer.Disconnected -= SmartServer_Disconnected;
               SmartServer.AddSymbol -= SmartServer_AddSymbol;
               SmartServer.AddPortfolio -= SmartServer_AddPortfolio;
               SmartServer.AddTick -= SmartServer_AddTick;
               SmartServer.AddTrade -= SmartServer_AddTrade;
               SmartServer.AddBar -= SmartServer_AddBar;
               SmartServer.UpdateBidAsk -= SmartServer_UpdateBidAsk;

               SmartServer.UpdateOrder -= SmartServer_UpdateOrder;
               SmartServer.OrderFailed -= SmartServer_OrderFailed;
               SmartServer.OrderSucceeded -= SmartServer_OrderSucceeded;
               SmartServer.OrderCancelFailed -= SmartServer_OrderCancelFailed;
               SmartServer.OrderCancelSucceeded -= SmartServer_OrderCancelSucceeded;
           }

           SmartServer = null;

           _startListeningPortfolios = false;

           _getPortfoliosAndSecurities = false;
       }

       /// <summary>
       /// блокиратор многопоточного доступа к серверу СмартКом
       /// </summary>
       private object _smartComServerLocker = new object(); 

       /// <summary>
       /// Сервер СмартКом
       /// </summary>
       public StServerClass SmartServer; 

// работа потока рассылки !!!!!

       /// <summary>
       /// очередь новых ордеров
       /// </summary>
       private ConcurrentQueue<Order> _ordersToSend;

       /// <summary>
       /// очередь тиков
       /// </summary>
       private ConcurrentQueue<List<Trade>> _tradesToSend;

       /// <summary>
       /// очередь новых портфелей
       /// </summary>
       private ConcurrentQueue<List<Portfolio>> _portfolioToSend;

       /// <summary>
       /// очередь новых инструментов
       /// </summary>
       private ConcurrentQueue<List<Security>> _securitiesToSend;

       /// <summary>
       /// очередь новых моих сделок
       /// </summary>
       private ConcurrentQueue<MyTrade> _myTradesToSend;

       /// <summary>
       /// очередь нового времени
       /// </summary>
       private ConcurrentQueue<DateTime> _newServerTime;

       /// <summary>
       /// очередь обновлённых серий свечек
       /// </summary>
       private ConcurrentQueue<CandleSeries> _candleSeriesToSend;

       /// <summary>
       /// очередь новых стаканов
       /// </summary>
       private ConcurrentQueue<MarketDepth> _marketDepthsToSend;

       /// <summary>
       /// очередь обновлений бида с аска по инструментам 
       /// </summary>
       private ConcurrentQueue<BidAskSender> _bidAskToSend;
       
       /// <summary>
       /// место в котором контролируется соединение
       /// </summary>
       private void SenderThreadArea()
       {
           while (true)
           {
               try
               {
                   if (_ordersToSend != null && _ordersToSend.Count != 0)
                   {
                       Order order;
                       if (_ordersToSend.TryDequeue(out order))
                       {
                           if (NewOrderIncomeEvent != null)
                           {
                               NewOrderIncomeEvent(order);
                           }
                       }
                   }
                   else if (_myTradesToSend != null && _myTradesToSend.Count != 0 &&
                  (_ordersToSend == null || _ordersToSend.Count == 0))
                   {
                       MyTrade myTrade;

                       if (_myTradesToSend.TryDequeue(out myTrade))
                       {
                           if (NewMyTradeEvent != null)
                           {
                               NewMyTradeEvent(myTrade);
                           }
                       }
                   }
                   else if (_tradesToSend != null && _tradesToSend.Count != 0)
                   {
                       List<Trade> trades;

                       if (_tradesToSend.TryDequeue(out trades))
                       {
                           if (NewTradeEvent != null)
                           {
                               NewTradeEvent(trades);
                           }
                       }
                   }

                   else if (_portfolioToSend != null && _portfolioToSend.Count != 0)
                   {
                       List<Portfolio> portfolio;

                       if (_portfolioToSend.TryDequeue(out portfolio))
                       {
                           if (PortfoliosChangeEvent != null)
                           {
                               PortfoliosChangeEvent(portfolio);
                           }
                       }
                   }

                   else if (_securitiesToSend != null && _securitiesToSend.Count != 0)
                   {
                       List<Security> security;

                       if (_securitiesToSend.TryDequeue(out security))
                       {
                           if (SecuritiesChangeEvent != null)
                           {
                               SecuritiesChangeEvent(security);
                           }
                       }
                   }
                   else if (_newServerTime != null && _newServerTime.Count != 0)
                   {
                      DateTime time;

                      if (_newServerTime.TryDequeue(out time))
                      {
                          if (TimeServerChangeEvent != null)
                          {
                              TimeServerChangeEvent(_serverTime);
                          }
                      }
                   }

                   else if (_candleSeriesToSend != null && _candleSeriesToSend.Count != 0)
                   {
                       CandleSeries series;

                       if (_candleSeriesToSend.TryDequeue(out series))
                       {
                           if (NewCandleIncomeEvent != null)
                           {
                               NewCandleIncomeEvent(series);
                           }
                       }
                   }

                   else if (_marketDepthsToSend != null && _marketDepthsToSend.Count != 0)
                   {
                       MarketDepth depth;

                       if (_marketDepthsToSend.TryDequeue(out depth))
                       {
                           if (NewMarketDepthEvent != null)
                           {
                               NewMarketDepthEvent(depth);
                           }
                       }
                   }

                   else if (_bidAskToSend != null && _bidAskToSend.Count != 0)
                   {
                       BidAskSender bidAsk;

                       if (_bidAskToSend.TryDequeue(out bidAsk))
                       {
                           if (NewBidAscIncomeEvent != null)
                           {
                               NewBidAscIncomeEvent(bidAsk.Bid, bidAsk.Ask, bidAsk.Security);
                           }
                       }
                   }
                   else
                   {
                       Thread.Sleep(1);
                   }
               }
               catch (Exception error)
               {
                   SendLogMessage(error.ToString(), LogMessageType.Error);
               }
           }
       }

// время сервера

       private DateTime _serverTime; 

       /// <summary>
       /// время сервера
       /// </summary>
       public DateTime ServerTime
       {
           get { return _serverTime; }

           private set
           {
               DateTime lastTime = _serverTime;
               _serverTime = value;

               if (_serverTime != lastTime )
               {
                   _newServerTime.Enqueue(_serverTime);
               }
           }
       }

       /// <summary>
       /// вызывается когда изменяется время сервера
       /// </summary>
       public event Action<DateTime> TimeServerChangeEvent;

// портфели

       private List<Portfolio> _portfolios;

       /// <summary>
       /// все счета в системе
       /// </summary>
       public List<Portfolio> Portfolios
       {
           get { return _portfolios; }
       }

       /// <summary>
       /// взять портфель по его номеру/имени
       /// </summary>
       public Portfolio GetPortfolioForName(string name)
       {
           try
           {
               if (_portfolios == null)
               {
                   return null;
               }
               return _portfolios.Find(portfolio => portfolio.Number == name);
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
               return null;
           }

       }

       private object _lockerUpdatePosition = new object();

        /// <summary>
        /// обновилась позиция на бирже
        /// </summary>
        /// <param name="portfolio">торговый счёт</param>
        /// <param name="symbol">название инструмента</param>
        /// <param name="avprice">средняя цена входа</param>
        /// <param name="amount">открытый объём</param>
        /// <param name="planned">объём с учётом выставленных заявок</param>
        private void SmartServer_UpdatePosition(string portfolio, string symbol, double avprice, double amount,
            double planned)
        {
            lock (_lockerUpdatePosition)
            {
                try
                {
                    if (_portfolios == null ||
                        _portfolios.Count == 0)
                    {
                        PositionSmartComSender peredast = new PositionSmartComSender();
                        peredast.Portfolio = portfolio;
                        peredast.Symbol = symbol;
                        peredast.Avprice = avprice;
                        peredast.Amount = amount;
                        peredast.Planned = planned;
                        peredast.PositionEvent += SmartServer_UpdatePosition;

                        Thread worker = new Thread(peredast.Sand);
                        worker.CurrentCulture = new CultureInfo("ru-RU");
                        worker.IsBackground = true;
                        worker.Start();
                        return;
                    }

                    Portfolio myPortfolio = _portfolios.Find(portfolio1 => portfolio1.Number == portfolio);

                    if (myPortfolio == null)
                    {
                        PositionSmartComSender peredast = new PositionSmartComSender();
                        peredast.Portfolio = portfolio;
                        peredast.Symbol = symbol;
                        peredast.Avprice = avprice;
                        peredast.Amount = amount;
                        peredast.Planned = planned;
                        peredast.PositionEvent += SmartServer_UpdatePosition;

                        Thread worker = new Thread(peredast.Sand);
                        worker.CurrentCulture = new CultureInfo("ru-RU");
                        worker.IsBackground = true;
                        worker.Start();
                        return;
                    }

                    PositionOnBoard position = new PositionOnBoard();
                    position.PortfolioName = portfolio;
                    position.SecurityNameCode = symbol;
                    position.ValueBegin = Convert.ToInt32(amount);
                    position.ValueCurrent = Convert.ToInt32(amount);
                    position.ValueBlocked = Convert.ToInt32(planned - amount);

                    myPortfolio.SetNewPosition(position);

                    _portfolioToSend.Enqueue(_portfolios);
                }
                catch (Exception error)
                {
                    SendLogMessage(error.ToString(), LogMessageType.Error);
                }   
            }
        }

       private object _lockerSetPortfolio = new object();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="portfolio">номер счёта</param>
        /// <param name="cash">доступные средства</param>
        /// <param name="leverage">плечё</param>
        /// <param name="comission">сумма комиссии</param>
        /// <param name="saldo">сальдо, т.е. итоговая прибыль сегодня</param>
        private void SmartServer_SetPortfolio(string portfolio, double cash, double leverage, double comission,
            double saldo)
        {
            lock (_lockerSetPortfolio)
            {
                try
                {
                    if (cash == 0 ||
                        saldo == 0)
                    {
                        PortfolioSmartComStateSender peredast = new PortfolioSmartComStateSender();
                        peredast.Portfolio = portfolio;
                        peredast.Cash = cash;
                        peredast.Leverage = leverage;
                        peredast.Comission = comission;
                        peredast.Saldo = saldo;
                        peredast.PortfolioEvent += SmartServer_SetPortfolio;

                        Thread worker = new Thread(peredast.Sand);
                        worker.CurrentCulture = new CultureInfo("ru-RU");
                        worker.IsBackground = true;
                        worker.Start();

                        return;
                    }

                    if (_portfolios == null ||
                        _portfolios.Count == 0)
                    {
                        PortfolioSmartComStateSender peredast = new PortfolioSmartComStateSender();
                        peredast.Portfolio = portfolio;
                        peredast.Cash = cash;
                        peredast.Leverage = leverage;
                        peredast.Comission = comission;
                        peredast.Saldo = saldo;
                        peredast.PortfolioEvent += SmartServer_SetPortfolio;

                        Thread worker = new Thread(peredast.Sand);
                        worker.CurrentCulture = new CultureInfo("ru-RU");
                        worker.IsBackground = true;
                        worker.Start();
                        return;
                    }

                    Portfolio myPortfolio = _portfolios.Find(portfolio1 => portfolio1.Number == portfolio);

                    if (myPortfolio == null)
                    {
                        return;
                    }

                    myPortfolio.ValueCurrent = Convert.ToDecimal(cash);
                    myPortfolio.ValueBegin = Convert.ToDecimal(saldo);
                    myPortfolio.ValueBlocked = 0;

                    if (myPortfolio.ValueCurrent != 0)
                    {
                        for (int i = 0; i < _portfolios.Count; i++)
                        {

                            if (_portfolios[i].ValueCurrent == 0)
                            {
                                _portfolios[i].ValueCurrent = Convert.ToDecimal(cash);
                                _portfolios[i].ValueBegin = Convert.ToDecimal(saldo);
                                _portfolios[i].ValueBlocked = 0;
                            }
                        }
                    }

                    _portfolioToSend.Enqueue(_portfolios);
                }
                catch (Exception error)
                {
                    SendLogMessage(error.ToString(), LogMessageType.Error);
                }
            }
        }

        /// <summary>
       /// из системы пришли новые портфели
       /// </summary>
       void SmartServer_AddPortfolio(int row, int nrows, string portfolioName, 
           string portfolioExch, StPortfolioStatus portfolioStatus)
       {
           try
           {
               if (_portfolios == null)
               {
                   _portfolios = new List<Portfolio>();
               }

               if (_portfolios.Find(portfolio => portfolio.Number == portfolioName) == null)
               {
                   SendLogMessage(portfolioName + "Содан новый портфель", LogMessageType.System);
                   _portfolios.Add(new Portfolio() { Number = portfolioName });
                   _portfolioToSend.Enqueue(_portfolios);
               }
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }
          
       }

       /// <summary>
       /// вызывается когда в системе появляются новые портфели
       /// </summary>
       public event Action<List<Portfolio>> PortfoliosChangeEvent;

// бумаги

       private List<Security> _securities;

       /// <summary>
       /// все инструменты в системе
       /// </summary>
       public List<Security> Securities
       {
           get { return _securities; }
       }

       /// <summary>
       /// взять инструмент в виде класса Security, по имени инструмента 
       /// </summary>
       public Security GetSecurityForName(string name)
       {
           if (_securities == null)
           {
               return null;
           }
           return _securities.Find(securiti => securiti.Name == name);
       }

       /// <summary>
       /// в системе появлились новые инструменты
       /// </summary>
       private void SmartServer_AddSymbol(int row, int nrows, string symbol, string shortName, string longName,
           string type, int decimals,
           int lotSize, double punkt, double step, string secExtId, string secExchName, DateTime expiryDate,
           double daysBeforeExpiry, double strike)
       {
           try
           {
               if (_securities == null)
               {
                   _securities = new List<Security>();
               }

               if (_securities.Find(securiti => securiti.Name == symbol) == null)
               {
                   Security security = new Security();
                   security.Name = symbol;
                   security.NameFull = longName;
                   security.NameClass = type;

                   security.Strike = Convert.ToDecimal(strike);
                   security.Expiration = expiryDate;

                   if (decimals == 5.0)
                   {
                       security.PriceStep = 0.00001m;
                       security.PriceStepCost= 0.00001m;
                   }
                   else if (decimals == 4.0)
                   {
                       security.PriceStep = 0.0001m;
                       security.PriceStepCost = 0.0001m;
                   }
                   else if (decimals == 3.0)
                   {
                       security.PriceStep = 0.001m;
                       security.PriceStepCost= 0.001m;
                   }
                   else if (decimals == 2.0)
                   {
                       security.PriceStep = 0.01m;
                       security.PriceStepCost= 0.01m;
                   }
                   else if (decimals == 1.0)
                   {
                       security.PriceStep = 0.1m;
                       security.PriceStepCost = 0.1m;
                   }
                   else if (decimals == 0)
                   {
                       security.PriceStep = Convert.ToDecimal(step);
                       security.PriceStepCost = Convert.ToDecimal(punkt);
                   }

                   if (type == "FUT")
                   {
                       security.Lot = 1;
                   }
                   else
                   {
                       security.Lot = lotSize;
                   }

                   security.PriceLimitLow = 0;
                   security.PriceLimitHigh = 0;
                   
                   _securities.Add(security);

                   _securitiesToSend.Enqueue(_securities);

               }
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }
       }

       /// <summary>
       /// вызывается при появлении новых инструментов
       /// </summary>
       public event Action<List<Security>> SecuritiesChangeEvent;

       /// <summary>
       /// показать инструменты 
       /// </summary>
       public void ShowSecuritiesDialog()
       {
           SecuritiesUi ui = new SecuritiesUi(this);
           ui.ShowDialog();
       }

// Подпись на данные

       /// <summary>
       /// мастер загрузки свечек
       /// </summary>
       private CandleManager _candleManager;

       /// <summary>
       /// объект блокирующий многопоточный доступ в StartThisSecurity
       /// </summary>
       private object _lockerStarter = new object();

        /// <summary>
        /// Начать выгрузку данных по инструменту. 
        /// </summary>
        /// <param name="namePaper">имя бумаги которую будем запускать</param>
        /// <param name="timeFrameBuilder">объект несущий в себе данные о таймФрейме</param>
        /// <returns>В случае удачи возвращает CandleSeries
        /// в случае неудачи null</returns>
        public CandleSeries StartThisSecurity(string namePaper, TimeFrameBuilder timeFrameBuilder)
       {
           try
           {
               if (_lastStartServerTime.AddSeconds(15) > DateTime.Now)
               {
                   return null;
               }

               // дальше по одному
               lock (_lockerStarter)
               {
                   if (namePaper == "")
                   {
                       return null;
                   }
                   // надо запустить сервер если он ещё отключен
                   if (ServerStatus != ServerConnectStatus.Connect)
                   {
                       //MessageBox.Show("Сервер не запущен. Скачивание данных прервано. Инструмент: " + namePaper);
                       return null;
                   }

                   if (_securities == null || _portfolios == null)
                   {
                       Thread.Sleep(5000);
                       return null;
                   }
                   if (_lastStartServerTime != DateTime.MinValue &&
                       _lastStartServerTime.AddSeconds(15) > DateTime.Now)
                   {
                       return null;
                   }

                   Security security = null;


                   for (int i = 0; _securities != null && i < _securities.Count; i++)
                   {
                       if (_securities[i].Name == namePaper)
                       {
                           security = _securities[i];
                           break;
                       }
                   }

                   if (security == null)
                   {
                       return null;
                   }

                   _candles = null;

                   CandleSeries series = new CandleSeries(timeFrameBuilder,security)
                   {
                       CandlesAll = _candles
                   };

                   lock (_smartComServerLocker)
                   {
                       SmartServer.ListenBidAsks(namePaper);
                       SmartServer.ListenQuotes(namePaper);
                       SmartServer.ListenTicks(namePaper);
                   }

                   Thread.Sleep(2000);

                   _candleManager.StartSeries(series);

                   SendLogMessage("Инструмент " + series.Security.Name + "ТаймФрейм" + series.TimeFrame +
               " успешно подключен на получение данных и прослушивание свечек", LogMessageType.System);

                   return series;
               }
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
               return null;
           }
       }

       /// <summary>
       /// остановить скачивание инструмента
       /// </summary>
       public void StopThisSecurity(CandleSeries series)
       {
           if (series != null)
           {
               _candleManager.StopSeries(series);
           }
       }

       /// <summary>
       /// изменились серии свечек
       /// </summary>
       private void _candleManager_CandleUpdateEvent(CandleSeries series)
       {
           _candleSeriesToSend.Enqueue(series);
       }

       /// <summary>
       /// вызывается в момент изменения серий свечек
       /// </summary>
       public event Action<CandleSeries> NewCandleIncomeEvent;

       /// <summary>
       /// коннекторам подключеным к серверу необходимо перезаказать данные
       /// </summary>
       public event Action NeadToReconnectEvent;

// свечи. Внутренняя тема смартКом

        /// <summary>
        /// взять свечи по инструменту
        /// </summary>
        /// <param name="security"> короткое название бумаги</param>
        /// <param name="timeSpan">таймФрейм</param>
        /// <param name="count">количество свечек</param>
        /// <returns>в случае неудачи вернётся null</returns>
        public List<Candle> GetSmartComCandleHistory(string security, TimeSpan timeSpan, int count)
       {
           if (timeSpan.TotalMinutes > 60 || 
               timeSpan.TotalMinutes< 1)
           {
               return null;
           }

           StBarInterval tf = StBarInterval.StBarInterval_Quarter;

           if (Convert.ToInt32(timeSpan.TotalMinutes) == 1)
           {
               tf = StBarInterval.StBarInterval_1Min;
           }
           else if (Convert.ToInt32(timeSpan.TotalMinutes) == 5)
           {
               tf = StBarInterval.StBarInterval_5Min;
           }
           else if (Convert.ToInt32(timeSpan.TotalMinutes) == 10)
           {
               tf = StBarInterval.StBarInterval_10Min;
           }
           else if (Convert.ToInt32(timeSpan.TotalMinutes) == 15)
           {
               tf = StBarInterval.StBarInterval_15Min;
           }
           else if (Convert.ToInt32(timeSpan.TotalMinutes) == 30)
           {
               tf = StBarInterval.StBarInterval_30Min;
           }
           else if (Convert.ToInt32(timeSpan.TotalMinutes) == 60)
           {
               tf = StBarInterval.StBarInterval_60Min;
           }

           if (tf == StBarInterval.StBarInterval_Quarter)
           {
               return null;
           }

           _candles = null;

           while (_candles == null)
           {
               lock (_smartComServerLocker)
               {
                   SmartServer.GetBars(security, tf, DateTime.Now.AddHours(6), count);
               }
           }

           return _candles;
       }

       /// <summary>
       /// свечи скаченные из метода GetSmartComCandleHistory
       /// </summary>
       private List<Candle> _candles; 

       /// <summary>
       /// входящие из системы свечи
       /// </summary>
       void SmartServer_AddBar(int row, int nrows, string symbol, StBarInterval interval, DateTime datetime, 
           double open, double high, double low, double close, double volume, double openInt)
       {
           Candle candle = new Candle();
           candle.Volume = Convert.ToInt32(volume);
           candle.Open = Convert.ToDecimal(open);
           candle.High = Convert.ToDecimal(high);
           candle.Low = Convert.ToDecimal(low);
           candle.Close = Convert.ToDecimal(close);

           if (interval == StBarInterval.StBarInterval_1Min) candle.TimeStart = datetime.AddMinutes(-1.0);
           else if (interval == StBarInterval.StBarInterval_5Min) candle.TimeStart = datetime.AddMinutes(-5.0);
           else if (interval == StBarInterval.StBarInterval_10Min) candle.TimeStart = datetime.AddMinutes(-10.0);
           else if (interval == StBarInterval.StBarInterval_15Min) candle.TimeStart = datetime.AddMinutes(-15.0);
           else if (interval == StBarInterval.StBarInterval_30Min) candle.TimeStart = datetime.AddMinutes(-30.0);
           else if (interval == StBarInterval.StBarInterval_60Min) candle.TimeStart = datetime.AddMinutes(-60.0);
           else candle.TimeStart = datetime;

           if (_candles == null || row == 0)
           {
               _candles = new List<Candle>();
           }

           _candles.Add(candle);
       }

// стакан

       /// <summary>
       /// все стаканы
       /// </summary>
       private List<MarketDepth> _depths;  

       /// <summary>
       /// входящий срез стакана
       /// </summary>
       void SmartServer_UpdateBidAsk(string symbol, int row, int nrows, double bid, double bidsize, double ask, double asksize)
       {
           MarketDepthLevel askOs = new MarketDepthLevel();
           askOs.Ask = Convert.ToDecimal(bidsize);
           askOs.Bid = 0;
           askOs.Price = Convert.ToDecimal(bid);

           MarketDepthLevel bidOs = new MarketDepthLevel();
           bidOs.Bid = Convert.ToDecimal(asksize);
           bidOs.Ask = 0;
           bidOs.Price = Convert.ToDecimal(ask);

           if (_depths == null)
           {
               _depths = new List<MarketDepth>();
           }

           MarketDepth myDepth = _depths.Find(depth => depth.SecurityNameCode == symbol);

           if (myDepth == null)
           {
               myDepth = new MarketDepth();
               myDepth.SecurityNameCode = symbol;
               _depths.Add(myDepth);
           }

           myDepth.Time = DateTime.Now;

           List<MarketDepthLevel> asks = myDepth.Asks;
           List<MarketDepthLevel> bids = myDepth.Bids;

           if (asks == null || asks.Count != nrows)
           {
               asks = new List<MarketDepthLevel>();
               bids = new List<MarketDepthLevel>();
               for (int i = 0; i < nrows; i++)
               {
                   asks.Add(new MarketDepthLevel());
                   bids.Add(new MarketDepthLevel());
               }
               myDepth.Asks = asks;
               myDepth.Bids = bids;
           }

           asks[row] = askOs;
           bids[row] = bidOs;

           if (row == nrows - 1 && NewMarketDepthEvent != null)
           {
               _marketDepthsToSend.Enqueue(myDepth);
               _bidAskToSend.Enqueue(new BidAskSender
               {
                   Bid = bids[0].Price,
                   Ask = asks[0].Price,
                   Security = GetSecurityForName(symbol)
               });
           }
       }

       /// <summary>
       /// вызывается когда изменяется бид или аск по инструменту
       /// </summary>
       public event Action<decimal, decimal, Security> NewBidAscIncomeEvent;

       /// <summary>
       /// вызывается когда изменяется стакан
       /// </summary>
       public event Action<MarketDepth> NewMarketDepthEvent;

// тики

       /// <summary>
       /// все тики
       /// </summary>
       private List<Trade>[] _allTrades;

       /// <summary>
       /// все тики имеющиеся у сервера
       /// </summary>
       public List<Trade>[] AllTrades { get { return _allTrades; } }

       /// <summary>
       /// блокиратор многопоточного доступа в SmartServer_AddTick
       /// </summary>
       private object _newTradesLoker = new object(); 

       /// <summary>
       /// входящие тики из системы
       /// </summary>
       void SmartServer_AddTick(string symbol, DateTime datetime, double price, double volume, string tradeno, StOrder_Action action)
       {
           try
           {
               lock (_newTradesLoker)
               {
                   Trade trade = new Trade();
                   trade.SecurityNameCode = symbol;
                   trade.Price = Convert.ToDecimal(price);
                   trade.Id = tradeno;
                   trade.Time = datetime;
                   trade.Volume = Convert.ToInt32(volume);
                   if (action == StOrder_Action.StOrder_Action_Buy)
                   {
                       trade.Side = Side.Buy;
                   }
                   if (action == StOrder_Action.StOrder_Action_Sell ||
                       action == StOrder_Action.StOrder_Action_Short)
                   {
                       trade.Side = Side.Sell;
                   }

                   // сохраняем
                   if (_allTrades == null)
                   {
                       _allTrades = new List<Trade>[1];
                       _allTrades[0] = new List<Trade> { trade };
                   }
                   else
                   {
                       // сортируем сделки по хранилищам
                       List<Trade> myList = null;
                       bool isSave = false;
                       for (int i = 0; i < _allTrades.Length; i++)
                       {
                           if (_allTrades[i] != null && _allTrades[i].Count != 0 &&
                               _allTrades[i][0].SecurityNameCode == trade.SecurityNameCode)
                           {
                               // если для этого инструметна уже есть хранилище, сохраняем и всё
                               _allTrades[i].Add(trade);
                               myList = _allTrades[i];
                               isSave = true;
                               break;
                           }
                       }

                       if (isSave == false)
                       {
                           // хранилища для инструмента нет
                           List<Trade>[] allTradesNew = new List<Trade>[_allTrades.Length + 1];
                           for (int i = 0; i < _allTrades.Length; i++)
                           {
                               allTradesNew[i] = _allTrades[i];
                           }
                           allTradesNew[allTradesNew.Length - 1] = new List<Trade>();
                           allTradesNew[allTradesNew.Length - 1].Add(trade);
                           myList = allTradesNew[allTradesNew.Length - 1];
                           _allTrades = allTradesNew;
                       }


                       _tradesToSend.Enqueue(myList);

                   }

                   // перегружаем последним временем тика время сервера
                   ServerTime = trade.Time;
               }
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }
          
       }

       /// <summary>
       /// взять тики по инструменту
       /// </summary>
       public List<Trade> GetAllTradesToSecurity(Security security)
       {
           if (_allTrades != null)
           {
               foreach (var tradesList in _allTrades)
               {
                   if (tradesList.Count > 1 &&
                       tradesList[0] != null &&
                       tradesList[0].SecurityNameCode == security.Name)
                   {
                       return tradesList;
                   }
               }    
           }

           return new List<Trade>();
       }

       /// <summary>
       /// вызывается в момет появления новых трейдов по инструменту
       /// </summary>
       public event Action<List<Trade>> NewTradeEvent;

// мои сделки

       private List<MyTrade> _myTrades;

       /// <summary>
       /// мои сделки
       /// </summary>
       public List<MyTrade> MyTrades
       {
           get { return _myTrades; }
       }

       /// <summary>
       /// входящие из системы мои сделки
       /// </summary>
       private void SmartServer_AddTrade(string portfolio, string symbol, string orderid, double price, double amount,
           DateTime datetime, string tradeno)
       {
           try
           {
               MyTrade trade = new MyTrade();
               trade.NumberTrade = tradeno;
               trade.SecurityNameCode = symbol;
               trade.NumberOrderParent = orderid;
               trade.Price = Convert.ToDecimal(price);
               trade.Volume = Convert.ToInt32(Math.Abs(amount));
               trade.Time = datetime;

               if (_myTrades == null)
               {
                   _myTrades = new List<MyTrade>();
               }
               _myTrades.Add(trade);

               _myTradesToSend.Enqueue(trade);
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }
       }

       /// <summary>
       /// вызывается когда приходит новая моя сделка
       /// </summary>
       public event Action<MyTrade> NewMyTradeEvent;

// работа с ордерами

       /// <summary>
       /// место работы потока на очередях исполнения заявок и их отмены
       /// </summary>
       private void ExecutorOrdersThreadArea()
       {
           while (true)
           {
               try
               {
                   Thread.Sleep(20);
                   if (_ordersToExecute != null && _ordersToExecute.Count != 0)
                   {
                       Order order;
                       if (_ordersToExecute.TryDequeue(out order))
                       {
                           StOrder_Action action;
                           if (order.Side == Side.Buy)
                           {
                               action = StOrder_Action.StOrder_Action_Buy;
                           }
                           else
                           {
                               action = StOrder_Action.StOrder_Action_Sell;
                           }

                           StOrder_Type type;

                           type = StOrder_Type.StOrder_Type_Limit;


                           StOrder_Validity validity = StOrder_Validity.StOrder_Validity_Day;

                           double price = Convert.ToDouble(order.Price);
                           double volume = Convert.ToDouble(order.Volume);
                           int cookie = Convert.ToInt32(order.NumberUser);

                           lock (_smartComServerLocker)
                           {
                               SmartServer.PlaceOrder(order.PortfolioNumber, order.SecurityNameCode, action, type, validity,
                                   price, volume, 0, cookie);
                           }
                       }
                   }
                   else if (_ordersToCansel != null && _ordersToCansel.Count != 0)
                   {
                       Order order;
                       if (_ordersToCansel.TryDequeue(out order))
                       {
                           lock (_smartComServerLocker)
                           {
                               SmartServer.CancelOrder(order.PortfolioNumber, order.SecurityNameCode, order.NumberMarket);
                           }
                       }
                   }
               }
               catch (Exception error)
               {
                   SendLogMessage(error.ToString(), LogMessageType.Error);
               }
           }
       }

       /// <summary>
       /// очередь ордеров для выставления в систему
       /// </summary>
       private ConcurrentQueue<Order> _ordersToExecute;

       /// <summary>
       /// очередь ордеров для отмены в системе
       /// </summary>
       private ConcurrentQueue<Order> _ordersToCansel; 

       /// <summary>
       /// исполнить ордер
       /// </summary>
       public void ExecuteOrder(Order order)
       {
           order.TimeCreate = ServerTime;
           _ordersToExecute.Enqueue(order);
       }

       /// <summary>
       /// отменить ордер
       /// </summary>
       public void CanselOrder(Order order)
       {
           _ordersToCansel.Enqueue(order);
       }

       /// <summary>
       /// входящий из системы ордер
       /// </summary>
       void SmartServer_UpdateOrder(string portfolio, string symbol, StOrder_State state, StOrder_Action action, StOrder_Type type,
           StOrder_Validity validity, double price, double amount, double stop, double filled, DateTime datetime,
           string orderid, string orderno, int statusMask, int cookie)
       {
           try
           {
               Order order = new Order();
               order.NumberUser = cookie;
               order.NumberMarket = orderid;
               order.SecurityNameCode = symbol;
               order.Price = Convert.ToDecimal(price);
               order.Volume = Convert.ToInt32(amount);
               order.VolumeExecute = Convert.ToInt32(amount) - Convert.ToInt32(filled);
               order.NumberUser = cookie;
               order.NumberMarket = orderno;
               order.PortfolioNumber = portfolio;

               if (state == StOrder_State.StOrder_State_Open ||
                   state == StOrder_State.StOrder_State_Submited)
               {
                   order.State = OrderStateType.Activ;
                   order.TimeCallBack = datetime;
               }
               if (state == StOrder_State.StOrder_State_Pending)
               {
                   order.TimeCallBack = datetime;
                   order.State = OrderStateType.Pending;
                   return;
               }
               if (state == StOrder_State.StOrder_State_Cancel ||
                   state == StOrder_State.StOrder_State_SystemCancel)
               {
                   order.TimeCancel = datetime;
                   order.State = OrderStateType.Cancel;
               }
               if (state == StOrder_State.StOrder_State_SystemReject)
               {
                   order.State = OrderStateType.Fail;
                   order.VolumeExecute = 0;
               }

               if (state == StOrder_State.StOrder_State_Filled)
               {
                   order.VolumeExecute = order.Volume;
                   order.State = OrderStateType.Done;
               }
               if (state == StOrder_State.StOrder_State_Partial)
               {
                   order.State = OrderStateType.Patrial;
               }


               if (action == StOrder_Action.StOrder_Action_Buy)
               {
                   order.Side = Side.Buy;
               }
               else
               {
                   order.Side = Side.Sell;
               }

               if (type == StOrder_Type.StOrder_Type_Limit)
               {
                   order.TypeOrder = OrderPriceType.Limit;
               }
               else
               {
                   order.TypeOrder = OrderPriceType.Market;
               }

               if (_ordersRegistred == null)
               {
                   _ordersRegistred = new List<Order>();
               }

               _ordersRegistred.Add(order);

               _ordersToSend.Enqueue(order);

               if (_myTrades != null &&
                       _myTrades.Count != 0)
               {
                   List<MyTrade> myTrade =
                       _myTrades.FindAll(trade => trade.NumberOrderParent == order.NumberMarket);

                   for (int tradeNum = 0; tradeNum < myTrade.Count; tradeNum++)
                   {
                       _myTradesToSend.Enqueue(myTrade[tradeNum]);
                   }
               }

           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }
       }

       private List<Order> _ordersRegistred; 

       /// <summary>
       /// во время выставления ордера произошла ошибка
       /// </summary>
       private void SmartServer_OrderFailed(int cookie, string orderid, string reason)
       {
           try
           {
               Order order = new Order();
               order.NumberUser = cookie;
               order.NumberMarket = orderid;
               order.State = OrderStateType.Fail;
               order.ServerType = ServerType.SmartCom;

               SendLogMessage(order.NumberUser + " Ошибка при отправке ордера " + reason, LogMessageType.Error);
               _ordersToSend.Enqueue(order);
           }
           catch (Exception error)
           {
               SendLogMessage(error.ToString(), LogMessageType.Error);
           }
       }

       /// <summary>
       /// ордер отозван из системы успешно
       /// </summary>
       void SmartServer_OrderCancelSucceeded(string orderid)
       {
           SendLogMessage(orderid + " Ордер отозван успешно", LogMessageType.System);
       }

       /// <summary>
       /// ошибка во время отзыва ордера из системы
       /// </summary>
       void SmartServer_OrderCancelFailed(string orderid)
       {
           SendLogMessage(orderid + " Ошибка при отзыве ордера", LogMessageType.Error);
       }

       /// <summary>
       /// ордер выставлен в систему успешно
       /// </summary>
       void SmartServer_OrderSucceeded(int cookie, string orderid)
       {
           SendLogMessage(cookie + " Ордер выставлен успешно", LogMessageType.System);
       }

       /// <summary>
       /// вызывается когда в системе появляется новый ордер
       /// </summary>
       public event Action<Order> NewOrderIncomeEvent;

// обработка лога

       /// <summary>
       /// добавить в лог новое сообщение
       /// </summary>
       private void SendLogMessage(string message, LogMessageType type)
       {
           if (LogMessageEvent != null)
           {
               LogMessageEvent(message, type);
           }
       }

       /// <summary>
       /// менеджер лога
       /// </summary>
       private Log _logMaster;

       /// <summary>
       /// исходящее сообщение для лога
       /// </summary>
       public event Action<string, LogMessageType> LogMessageEvent;
    }

    public class PositionSmartComSender
    {
        public string Portfolio;
        public string Symbol;
        public double Avprice;
        public double Amount;
        public double Planned;

        public void Sand()
        {
            Thread.Sleep(5000);
            if (PositionEvent != null)
            {
                PositionEvent(Portfolio,Symbol,Avprice,Amount,Planned);
            }
        }

        public event Action<string,string,double,double,double> PositionEvent;
    }

    public class PortfolioSmartComStateSender
    {
        public string Portfolio;
        public double Cash;
        public double Leverage;
        public double Comission;
        public double Saldo;

        public void Sand()
        {
            Thread.Sleep(5000);
            if (PortfolioEvent != null)
            {
                PortfolioEvent(Portfolio,Cash,Leverage,Comission,Saldo);
            }
        }

        public event Action<string,double,double,double,double> PortfolioEvent;
    }

}

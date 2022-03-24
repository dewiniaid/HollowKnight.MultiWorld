﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MultiWorldLib.Messaging;
using MultiWorldLib.Binary;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using MultiWorldLib.Messaging.Definitions.Messages;

using System.IO;
using MultiWorldLib;

namespace MultiWorldServer
{
    class Server
    {
        const int PingInterval = 10000; //In milliseconds

        private ulong nextUID = 1;
        private readonly MWMessagePacker Packer = new MWMessagePacker(new BinaryMWMessageEncoder());
        private readonly List<Client> Unidentified = new List<Client>();

        private readonly Timer PingTimer, ResendTimer;

        private readonly object _clientLock = new object();
        private readonly Dictionary<ulong, Client> Clients = new Dictionary<ulong, Client>();
        private readonly Dictionary<int, GameSession> GameSessions = new Dictionary<int, GameSession>();
        private readonly Dictionary<string, Dictionary<ulong, int>> readiedRooms = new Dictionary<string, Dictionary<ulong, int>>();
        private readonly Dictionary<string, Mode> roomsMode = new Dictionary<string, Mode>();
        private readonly Dictionary<string, int> roomsHash = new Dictionary<string, int>();
        private readonly Dictionary<string, Dictionary<ulong, PlayerItemsPool>> gameGeneratingRooms = new Dictionary<string, Dictionary<ulong, PlayerItemsPool>>();
        private readonly Dictionary<string, int> generatingSeeds = new Dictionary<string, int>();
        internal static Action<ulong, MWMessage> QueuePushMessage;
        private readonly TcpListener _server;

        private static StreamWriter LogWriter;

        public bool Running { get; private set; }

        public Server(int port)
        {
            //Listen on any ip
            _server = new TcpListener(IPAddress.Parse("0.0.0.0"), port);
            _server.Start();

            //_readThread = new Thread(ReadWorker);
            //_readThread.Start();
            _server.BeginAcceptTcpClient(AcceptClient, _server);
            PingTimer = new Timer(DoPing, Clients, 1000, PingInterval);
            ResendTimer = new Timer(DoResends, Clients, 1000, 10000);
            Running = true;
            Log($"Server started on port {port}!");
            QueuePushMessage = AddPushMessage;
        }

        internal static void OpenLogger(string filename)
        {
            if (!Directory.Exists("Logs"))
            {
                Directory.CreateDirectory("Logs");
            }
            filename = "Logs/" + filename + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            FileStream fileStream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            LogWriter = new StreamWriter(fileStream, Encoding.UTF8) { AutoFlush = true };
        }

        internal static void Log(string message, int? session = null)
        {
            if (session == null)
            {
                LogWriter.WriteLine($"[{DateTime.Now.ToLongTimeString()}] {message}");
            }
            else
            {
                LogWriter.WriteLine($"[{DateTime.Now.ToLongTimeString()}] [{session}] {message}");
            }
        }

        internal static void LogDebug(string message, int? session = null)
        {
#if DEBUG
            Log(message, session);
#endif
        }

        internal static void LogToConsole(string message)
        {
            Console.WriteLine(message);
            Log(message);
        }

        public void GiveItem(string item, int session, int player)
        {
            LogToConsole($"Giving item {item} to player {player} in session {session}");

            if (item == null)
            {
                LogToConsole($"Invalid item: {item}");
                return;
            }

            if (!GameSessions.ContainsKey(session))
            {
                LogToConsole($"Session {session} does not exist");
                return;
            }

            GameSessions[session].SendItemTo(player, LanguageStringManager.AddItemId(item, Consts.SERVER_GENERIC_ITEM_ID), "Server");
        }

        public void ListSessions()
        {
            LogToConsole($"{GameSessions.Count} current sessions");
            foreach (var kvp in GameSessions)
            {
                LogToConsole($"ID: {kvp.Key} players: {kvp.Value.GetPlayerString()}");
            }
        }

        public void ListReady()
        {
            LogToConsole($"{readiedRooms.Count} current lobbies");
            foreach (var kvp in readiedRooms)
            {
                string playerString = string.Join(", ", kvp.Value.Keys.Select((uid) => Clients[uid].Nickname).ToArray());
                LogToConsole($"Room: {kvp.Key} players: {playerString}");
            }
        }

        private void DoPing(object clients)
        {
            lock (_clientLock)
            {
                var Now = DateTime.Now;
                List<Client> clientList = Clients.Values.ToList();
                for (int i = clientList.Count - 1; i >= 0; i--)
                {
                    Client client = clientList[i];
                    //If a client has missed 3 pings we disconnect them
                    if (Now - client.lastPing > TimeSpan.FromMilliseconds(PingInterval * 3.5))
                    {
                        Log(string.Format("Client {0} timed out. ({1})", client.UID, client.Session?.Name));
                        DisconnectClient(client);
                    }
                    else
                        SendMessage(new MWPingMessage(), client);
                }
            }
        }

        private void DoResends(object clients)
        {
            try
            {
                lock (_clientLock)
                {
                    var ClientsList = Clients.Values.ToList();
                    foreach (var client in ClientsList)
                    {
                        if (client.Session != null)
                        {
                            lock (client.Session.MessagesToConfirm)
                            {
                                List<MWMessage> messages = new List<MWMessage>();
                                foreach (MWMessage message in client.Session.MessagesToConfirm) messages.Add(message);
                                SendMessages(messages.ToArray(), client);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // I don't really like doing this but I was occasionally getting NullRefeneceExceptions here
                Log($"Error resending items: {e.Message}");
            }
        }

        private void StartReadThread(Client c)
        {
            //Check that we aren't already reading
            if (c.ReadWorker != null)
                return;
            var start = new ParameterizedThreadStart(ReadWorker);
            c.ReadWorker = new Thread(start);
            c.ReadWorker.Start(c);
        }

        private void ReadWorker(object boxedClient)
        {
            if (boxedClient == null) return;

            Client client = (Client)boxedClient;
            NetworkStream stream = client.TcpClient.GetStream();
            try
            {
                while (client.TcpClient.Connected)
                {
                    MWPackedMessage message = new MWPackedMessage(stream);
                    ReadFromClient(client, message);
                }
            }
            catch (Exception e)
            {
                Log($"Exception thrown while reading message: {e.Message}");
                Log(e.StackTrace);
                DisconnectClient(client);
            }
        }

        private void AcceptClient(IAsyncResult res)
        {
            try
            {
                Client client = new Client
                {
                    TcpClient = _server.EndAcceptTcpClient(res)
                };

                _server.BeginAcceptTcpClient(AcceptClient, _server);

                if (!client.TcpClient.Connected)
                {
                    return;
                }

                client.TcpClient.ReceiveTimeout = 2000;
                client.TcpClient.SendTimeout = 2000;
                client.lastPing = DateTime.Now;

                lock (_clientLock)
                {
                    Unidentified.Add(client);
                }

                StartReadThread(client);
            }
            catch (Exception e) // Not sure what could throw here, but have been seeing random rare exceptions in the servers
            {
                Log("Error when accepting client: " + e.Message);
            }
        }

        internal bool SendMessage(MWMessage message, Client client)
        {
            if (client?.TcpClient == null || !client.TcpClient.Connected)
            {
                //Log("Returning early due to client not connected");
                return false;
            }

            try
            {
                SendMessageUnsafe(message, client);
                return true;
            }
            catch (Exception e)
            {
                Log($"Failed to send message to '{client.Session?.Name}':\n{e}\nDisconnecting");
                DisconnectClient(client);
                return false;
            }
        }

        internal void SendMessageUnsafe(MWMessage message, Client client)
        {
            byte[] bytes = Packer.Pack(message).Buffer;
            lock (client.TcpClient)
            {
                NetworkStream stream = client.TcpClient.GetStream();
                stream.WriteTimeout = 2000;
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        internal bool SendMessages(MWMessage[] messages, Client client)
        {
            if (client?.TcpClient == null || !client.TcpClient.Connected)
            {
                //Log("Returning early due to client not connected");
                return false;
            }

            try
            {
                List<byte> messagesBytes = new List<byte>();
                foreach (MWMessage message in messages)
                {
                    messagesBytes.AddRange(Packer.Pack(message).Buffer);
                }
                lock (client.TcpClient)
                {
                    NetworkStream stream = client.TcpClient.GetStream();
                    stream.WriteTimeout = 2000;
                    stream.Write(messagesBytes.ToArray(), 0, messagesBytes.Count());
                }
                return true;
            }
            catch (Exception e)
            {
                Log($"Failed to send messages to '{client.Session?.Name}':\n{e}\nDisconnecting");
                DisconnectClient(client);
                return false;
            }
        }

        private void DisconnectClient(Client client)
        {
            Log(string.Format("Disconnecting UID {0}", client.UID));
            try
            {
                //Remove first from lists so if we get a network exception at least on the server side stuff should be clean
                Unready(client.UID);
                lock (_clientLock)
                {
                    Clients.Remove(client.UID);
                    Unidentified.Remove(client);
                    RemovePlayerFromSession(client);
                }

                if (client.TcpClient.Connected)
                    SendMessageUnsafe(new MWDisconnectMessage(), client);
                //Wait a bit to give the message a chance to be sent at least before closing the client
                Thread.Sleep(10);
            }
            catch (Exception e)
            {
                //Do nothing, we're already disconnecting
                Log("Exception disconnecting client: " + e);
            }
            finally
            {
                client.TcpClient.Close();
            }
        }

        private void RemovePlayerFromSession(Client client)
        {
            lock (_clientLock)
            {
                if (client.Session != null)
                {
                    GameSessions[client.Session.randoId].RemovePlayer(client);
                    client.Session = null;
                }
            }
        }

        private void ReadFromClient(Client sender, MWPackedMessage packed)
        {
            MWMessage message;
            try
            {
                message = Packer.Unpack(packed);
            }
            catch (Exception e)
            {
                Log(e.ToString());
                return;
            }

            switch (message.MessageType)
            {
                case MWMessageType.SharedCore:
                    break;
                case MWMessageType.ConnectMessage:
                    HandleConnect(sender, (MWConnectMessage)message);
                    break;
                case MWMessageType.ReconnectMessage:
                    break;
                case MWMessageType.DisconnectMessage:
                    HandleDisconnect(sender, (MWDisconnectMessage)message);
                    break;
                case MWMessageType.JoinMessage:
                    HandleJoin(sender, (MWJoinMessage)message);
                    break;
                case MWMessageType.JoinConfirmMessage:
                    break;
                case MWMessageType.LeaveMessage:
                    HandleLeaveMessage(sender, (MWLeaveMessage)message);
                    break;
                case MWMessageType.ItemReceiveMessage:
                    break;
                case MWMessageType.ItemReceiveConfirmMessage:
                    HandleItemReceiveConfirm(sender, (MWItemReceiveConfirmMessage)message);
                    break;
                case MWMessageType.ItemSendMessage:
                    HandleItemSend(sender, (MWItemSendMessage)message);
                    break;
                case MWMessageType.ItemsSendMessage:
                    HandleItemsSend(sender, (MWItemsSendMessage)message);
                    break;
                case MWMessageType.ItemSendConfirmMessage:
                    break;
                case MWMessageType.NotifyMessage:
                    HandleNotify(sender, (MWNotifyMessage)message);
                    break;
                case MWMessageType.PingMessage:
                    HandlePing(sender, (MWPingMessage)message);
                    break;
                case MWMessageType.ReadyMessage:
                    HandleReadyMessage(sender, (MWReadyMessage)message);
                    break;
                case MWMessageType.ISReadyMessage:
                    HandleISReadyMessage(sender, (ISReadyMessage)message);
                    break;
                case MWMessageType.UnreadyMessage:
                    HandleUnreadyMessage(sender, (MWUnreadyMessage)message);
                    break;
                case MWMessageType.InitiateGameMessage:
                    HandleInitiateGameMessage(sender, (MWInitiateGameMessage)message);
                    break;
                case MWMessageType.InitiateSyncGameMessage:
                    HandleInitiateSyncGameMessage(sender, (MWInitiateSyncGameMessage)message);
                    break;
                case MWMessageType.RequestSettingsMessage:
                    HandleRequestSettingsMessage(sender, (MWRequestSettingsMessage)message);
                    break;
                case MWMessageType.ApplySettingsMessage:
                    HandleApplySettingsMessage(sender, (MWApplySettingsMessage)message);
                    break;
                case MWMessageType.RandoGeneratedMessage:
                    HandleRandoGeneratedMessage(sender, (MWRandoGeneratedMessage)message);
                    break;
                case MWMessageType.SaveMessage:
                    HandleSaveMessage(sender, (MWSaveMessage)message);
                    break;
                case MWMessageType.AnnounceCharmNotchCostsMessage:
                    HandleAnnounceCharmNotchCostsMessage(sender, (MWAnnounceCharmNotchCostsMessage)message);
                    break;
                case MWMessageType.ConfirmCharmNotchCostsReceivedMessage:
                    HandleConfirmCharmNotchCostsReceivedMessage(sender, (MWConfirmCharmNotchCostsReceivedMessage)message);
                    break;
                case MWMessageType.VisitStateChangedMessage:
                    HandleVisitStateChangedMessage(sender, (MWVisitStateChangedMessage)message);
                    break;
                case MWMessageType.VisitStateChangedConfirmMessage:
                    HandleVisitStateChangedConfirmMessage(sender, (MWVisitStateChangedConfirmMessage)message);
                    break;
                case MWMessageType.TransitionFoundMessage:
                    HandleTransitionFoundMessage(sender, (MWTransitionFoundMessage)message);
                    break;
                case MWMessageType.TransitionFoundConfirmMessage:
                    HandleTransitionFoundConfirmMessage(sender, (MWTransitionFoundConfirmMessage)message);
                    break;
                case MWMessageType.InvalidMessage:
                default:
                    throw new InvalidOperationException("Received Invalid Message Type");
            }
        }

        private void HandleConnect(Client sender, MWConnectMessage message)
        {
            lock (_clientLock)
            {
                if (Unidentified.Contains(sender))
                {
                    if (message.SenderUid == 0)
                    {
                        sender.UID = nextUID++;
                        Log(string.Format("Assigned UID={0}", sender.UID));
                        SendMessage(new MWConnectMessage { SenderUid = sender.UID }, sender);
                        Log("Connect sent!");
                        Clients.Add(sender.UID, sender);
                        Unidentified.Remove(sender);
                    }
                    else
                    {
                        Unidentified.Remove(sender);
                        sender.TcpClient.Close();
                    }
                }
            }
        }

        private void HandleDisconnect(Client sender, MWDisconnectMessage message)
        {
            DisconnectClient(sender);
        }

        private void HandlePing(Client sender, MWPingMessage message)
        {
            sender.lastPing = DateTime.Now;
        }

        private void HandleJoin(Client sender, MWJoinMessage message)
        {
            lock (_clientLock)
            {
                if (!Clients.ContainsKey(sender.UID))
                {
                    return;
                }

                if (!GameSessions.ContainsKey(message.RandoId))
                {
                    Log($"Starting session for rando id: {message.RandoId}");
                    GameSessions[message.RandoId] = new GameSession(message.RandoId, message.Mode == Mode.ItemSync);
                }

                GameSessions[message.RandoId].AddPlayer(sender, message);
                SendMessage(new MWJoinConfirmMessage(), sender);
            }
        }

        private void HandleLeaveMessage(Client sender, MWLeaveMessage message)
        {
            RemovePlayerFromSession(sender);
        }

        private void HandleReadyMessage(Client sender, MWReadyMessage message)
        {
            sender.Nickname = message.Nickname;
            sender.Room = message.Room;
            lock (_clientLock)
            {
                string roomText = string.IsNullOrEmpty(sender.Room) ? "default room" : $"room \"{sender.Room}\"";

                if (!readiedRooms.ContainsKey(sender.Room))
                {
                    readiedRooms[sender.Room] = new Dictionary<ulong, int>();
                    roomsMode[sender.Room] = message.ReadyMode;
                    Log($"{roomText} room created for {message.ReadyMode}");
                }

                int readyId = (new Random()).Next();
                readiedRooms[sender.Room][sender.UID] = readyId;

                Log($"{sender.Nickname} (UID {sender.UID}) readied up in {roomText} ({readiedRooms[sender.Room].Count} readied)");

                string names = string.Join(", ", readiedRooms[sender.Room].Keys.Select((uid) => Clients[uid].Nickname).ToArray());

                foreach (ulong uid in readiedRooms[sender.Room].Keys)
                {
                    SendMessage(new MWReadyConfirmMessage { Ready = readiedRooms[sender.Room].Count, Names = names, ReadyID = readyId }, Clients[uid]);
                }
            }
        }

        private void HandleISReadyMessage(Client sender, ISReadyMessage message)
        {
            sender.Nickname = message.Nickname;
            sender.Room = message.Room;
            lock (_clientLock)
            {
                string roomText = string.IsNullOrEmpty(sender.Room) ? "default room" : $"room \"{sender.Room}\"";

                if (!readiedRooms.ContainsKey(sender.Room))
                {
                    readiedRooms[sender.Room] = new Dictionary<ulong, int>();
                    roomsMode[sender.Room] = Mode.ItemSync;
                    roomsHash[sender.Room] = message.Hash;
                    Log($"{roomText} room created for {roomsMode[sender.Room]}");
                }
                else if (roomsMode[sender.Room] != Mode.ItemSync)
                {
                    SendMessage(new MWReadyDenyMessage { Description = $"Room originally created for {roomsMode[sender.Room]}.\nPlease choose a different room name" }, sender);
                    return;
                }
                else if (!roomsHash.ContainsKey(sender.Room))
                {
                    roomsHash[sender.Room] = message.Hash;
                }
                else if (roomsHash[sender.Room] != message.Hash)
                {
                    SendMessage(new MWReadyDenyMessage { Description = "Hash mismatch to the rest of the room.\nPlease verify your settings" }, sender);
                    return;
                }

                int readyId = (new Random()).Next();
                readiedRooms[sender.Room][sender.UID] = readyId;

                Log($"{sender.Nickname} (UID {sender.UID}) readied up in {roomText} ({readiedRooms[sender.Room].Count} readied)");

                string names = string.Join(", ", readiedRooms[sender.Room].Keys.Select((uid) => Clients[uid].Nickname).ToArray());

                foreach (ulong uid in readiedRooms[sender.Room].Keys)
                {
                    SendMessage(new MWReadyConfirmMessage { Ready = readiedRooms[sender.Room].Count, Names = names, ReadyID = readyId }, Clients[uid]);
                }
            }
        }

        private void Unready(ulong uid)
        {
            if (!Clients.TryGetValue(uid, out Client c)) return;

            lock (_clientLock)
            {
                if (c.Room == null || !readiedRooms.ContainsKey(c.Room) || !readiedRooms[c.Room].ContainsKey(uid)) return;
                string roomText = string.IsNullOrEmpty(c.Room) ? "default room" : $"room \"{c.Room}\"";
                Log($"{c.Nickname} (UID {c.UID}) unreadied from {roomText} ({readiedRooms[c.Room].Count - 1} readied)");

                readiedRooms[c.Room].Remove(c.UID);
                if (readiedRooms[c.Room].Count == 0)
                {
                    readiedRooms.Remove(c.Room);
                    roomsMode.Remove(c.Room);
                    roomsHash.Remove(c.Room);
                    return;
                }

                string names = "";
                foreach (ulong uid2 in readiedRooms[c.Room].Keys)
                {
                    names += Clients[uid2].Nickname;
                    names += ", ";
                }

                if (names.Length >= 2)
                {
                    names = names.Substring(0, names.Length - 2);
                }

                foreach (var kvp in readiedRooms[c.Room])
                {
                    if (!Clients.ContainsKey(kvp.Key)) continue;
                    SendMessage(new MWReadyConfirmMessage { Ready = readiedRooms[c.Room].Count, Names = names, ReadyID = kvp.Value }, Clients[kvp.Key]);
                }
            }
        }

        private void HandleUnreadyMessage(Client sender, MWUnreadyMessage message)
        {
            Unready(sender.UID);
        }

        private void HandleSaveMessage(Client sender, MWSaveMessage message)
        {
            if (sender.Session == null) return;

            GameSessions[sender.Session.randoId].Save(sender.Session.playerId);
        }

        private void HandleInitiateGameMessage(Client sender, MWInitiateGameMessage message)
        {
            string room = sender.Room;

            lock (_clientLock)
            {
                if (room == null || !readiedRooms.ContainsKey(room) || !readiedRooms[room].ContainsKey(sender.UID)) return;
                if (gameGeneratingRooms.ContainsKey(room)) return;

                gameGeneratingRooms[room] = new Dictionary<ulong, PlayerItemsPool>();
                generatingSeeds[room] = message.Seed;
            }

            foreach (var kvp in readiedRooms[room])
            {
                Client client = Clients[kvp.Key];
                SendMessage(new MWRequestRandoMessage(), client);
            }
        }

        private void HandleRequestSettingsMessage(Client sender, MWRequestSettingsMessage message)
        {
            lock (_clientLock)
            {
                if (sender.Room == null || !readiedRooms.ContainsKey(sender.Room) || 
                    !readiedRooms[sender.Room].ContainsKey(sender.UID) || readiedRooms[sender.Room].Count == 1)
                    return;

                SendMessage(message, Clients[readiedRooms[sender.Room].Keys.Where(key => key != sender.UID).First()]);
            }
        }

        private void HandleApplySettingsMessage(Client sender, MWApplySettingsMessage message)
        {
            lock (_clientLock)
            {
                if (!readiedRooms.ContainsKey(sender.Room) || !readiedRooms[sender.Room].ContainsKey(sender.UID)) return;

                foreach (ulong uid in readiedRooms[sender.Room].Keys)
                {
                    if (sender.UID != uid)
                        SendMessage(message, Clients[uid]);
                }
            }
        }

        private void HandleInitiateSyncGameMessage(Client sender, MWInitiateSyncGameMessage message)
        {
            string room = sender.Room;
            lock (_clientLock)
            {
                if (room == null || !readiedRooms.ContainsKey(room) || !readiedRooms[room].ContainsKey(sender.UID)) return;

                List<Client> clients = readiedRooms[room].Select(kvp => Clients[kvp.Key]).ToList();

                Log("Starting Sync game");
                clients.Where(client => sender.UID != client.UID).ToList().
                    ForEach(client => SendMessage(message, client));

                int randoId = new Random().Next();
                GameSessions[randoId] = new GameSession(randoId, Enumerable.Range(0, readiedRooms[room].Count).ToList(), true);

                List<string> nicknames = new List<string>();
                clients.ForEach(client => nicknames.Add(client.Nickname));

                (string, string)[] emptyList = new (string, string)[0];
                foreach ((var client, int i) in clients.Select((c, index) => (c, index)))
                {
                    ResultData resultData = new ResultData
                    {
                        randoId = randoId,
                        playerId = i,
                        nicknames = null,
                        ItemsSpoiler = "",
                        PlayerItems = emptyList
                    };

                    Log($"Sending game data to player {i} - {client.Nickname}");
                    SendMessage(new MWResultMessage { Placements = emptyList, ResultData = resultData }, client);
                }

                readiedRooms.Remove(room);
                roomsMode.Remove(room);
                roomsHash.Remove(room);
            }
        }

        private void HandleRandoGeneratedMessage(Client sender, MWRandoGeneratedMessage message)
        {
            string room = sender.Room;

            lock (_clientLock)
            {
                if (room == null || !readiedRooms.ContainsKey(room) || !readiedRooms[room].ContainsKey(sender.UID)) return;
                if (!gameGeneratingRooms.ContainsKey(room) || gameGeneratingRooms[room].ContainsKey(sender.UID)) return;

                Log($"Adding {sender.Nickname}'s generated rando");
                gameGeneratingRooms[room][sender.UID] = new PlayerItemsPool(readiedRooms[room][sender.UID], message.Items, sender.Nickname);
                if (gameGeneratingRooms[room].Count < readiedRooms[room].Count) return;
            }

            List<Client> clients = new List<Client>();
            List<int> readyIds = new List<int>();
            List<string> nicknames = new List<string>();

            string roomText = string.IsNullOrEmpty(sender.Room) ? "default room" : $"room \"{sender.Room}\"";
            Log($"Starting MW Calculation for {roomText}");

            lock (_clientLock)
            {
                if (!readiedRooms[room].ContainsKey(sender.UID)) return;

                foreach (var kvp in readiedRooms[room])
                {
                    clients.Add(Clients[kvp.Key]);
                    readyIds.Add(kvp.Value);
                    nicknames.Add(Clients[kvp.Key].Nickname);
                    Log(Clients[kvp.Key].Nickname + "'s Ready id is " + kvp.Value);
                }
            }

            int randoId = new Random().Next();
            Log($"Starting rando {randoId} with players: {string.Join(", ", nicknames.ToArray())}");
            Log("Randomizing world...");

            ItemsRandomizer itemsRandomizer = new ItemsRandomizer(
                gameGeneratingRooms[room].Select(kvp => kvp.Value).ToList(),
                generatingSeeds[room]);

            List<PlayerItemsPool> playersItemsPools = itemsRandomizer.Randomize();
            Log("Done randomization");

            string spoilerLocalPath = $"Spoilers/{randoId}.txt";
            string itemsSpoiler = ""; // ItemsSpoilerLogger.GetLog(playersItemsPools);
            SaveItemSpoilerFile(spoilerLocalPath, itemsSpoiler, generatingSeeds[room]);
            Log($"Done generating spoiler log");

            GameSessions[randoId] = new GameSession(randoId, Enumerable.Range(0, playersItemsPools.Count).ToList(), false);
            string[] gameNicknames = playersItemsPools.Select(pip => pip.Nickname).ToArray();

            for (int i = 0; i < playersItemsPools.Count; i++)
            {
                ResultData resultData = new ResultData
                {
                    randoId = randoId,
                    playerId = i,
                    nicknames = gameNicknames,
                    PlayerItems = itemsRandomizer.GetPlayerItems(i).ToArray(),
                    ItemsSpoiler = itemsSpoiler
                };
                int previouslyUsedIndex = nicknames.IndexOf(playersItemsPools[i].Nickname);
                
                Log($"Sending result to player {playersItemsPools[i].PlayerId} - {playersItemsPools[i].Nickname}");
                var client = clients.Find(_client => readiedRooms[room][_client.UID] == playersItemsPools[i].ReadyId);
                SendMessage(new MWResultMessage { Placements = playersItemsPools[i].ItemsPool, ResultData = resultData }, client);
            }

            lock (_clientLock)
            {
                readiedRooms.Remove(room);
                roomsMode.Remove(room);
                roomsHash.Remove(room);
                gameGeneratingRooms.Remove(room);
                generatingSeeds.Remove(room);
            }
        }

        private void SaveItemSpoilerFile(string path, string itemsSpoiler, int seed)
        {
            if (!Directory.Exists("Spoilers"))
            {
                Directory.CreateDirectory("Spoilers");
            }

            if (File.Exists(path) && false)
            {
                File.Create(path).Dispose();
            }
            itemsSpoiler = "MultiWorld generated with seed " + seed + Environment.NewLine + itemsSpoiler;
            File.WriteAllText(path, itemsSpoiler);
        }

        private void HandleNotify(Client sender, MWNotifyMessage message)
        {
            Log($"[{sender.Session?.Name}]: {message.Message}");
        }

        private void HandleItemReceiveConfirm(Client sender, MWItemReceiveConfirmMessage message)
        {
            List<MWConfirmableMessage> confirmed = sender.Session.ConfirmMessage(message);

            foreach (MWConfirmableMessage msg in confirmed)
            {
                switch (msg.MessageType)
                {
                    case MWMessageType.ItemReceiveMessage:
                        MWItemReceiveMessage itemMsg = (MWItemReceiveMessage)msg;
                        GameSessions[sender.Session.randoId].ConfirmItem(sender.Session.playerId, itemMsg);
                        break;
                    default:
                        break;
                }
            }
        }

        private void HandleVisitStateChangedConfirmMessage(Client sender, MWVisitStateChangedConfirmMessage message)
        {
            List<MWConfirmableMessage> confirmed = sender.Session.ConfirmMessage(message);

            foreach (MWConfirmableMessage msg in confirmed)
            {
                switch (msg.MessageType)
                {
                    case MWMessageType.VisitStateChangedMessage:
                        MWVisitStateChangedMessage visitStateChangedMsg = (MWVisitStateChangedMessage)msg;
                        GameSessions[sender.Session.randoId].ConfirmVisitStateChanged(sender.Session.playerId, visitStateChangedMsg);
                        break;
                    default:
                        break;
                }
            }
        }

        private void HandleTransitionFoundConfirmMessage(Client sender, MWTransitionFoundConfirmMessage message)
        {
            List<MWConfirmableMessage> confirmed = sender.Session.ConfirmMessage(message);

            foreach (MWConfirmableMessage msg in confirmed)
            {
                switch (msg.MessageType)
                {
                    case MWMessageType.TransitionFoundMessage:
                        MWTransitionFoundMessage transitionFoundMessage = (MWTransitionFoundMessage)msg;
                        GameSessions[sender.Session.randoId].ConfirmTransitionFound(sender.Session.playerId, transitionFoundMessage);
                        break;
                    default:
                        break;
                }
            }
        }

        private void HandleItemSend(Client sender, MWItemSendMessage message)
        {
            // ItemSync support lies here
            if (message.To == -2)
            {
                GameSessions[sender.Session.randoId].SendItemToAll(message.Item, sender.Session.playerId);
            }
            else
            {
                GameSessions[sender.Session.randoId].SendItemTo(message.To, message.Item, sender.Session.playerId);
            }

            // Confirm sending the item to the sender
            SendMessage(new MWItemSendConfirmMessage { Item = message.Item, To = message.To }, sender);
        }

        private void HandleItemsSend(Client sender, MWItemsSendMessage message)
        {
            if (sender.Session == null) return;  // Throw error?
            Log($"{sender.Session.Name} ejected, sending {message.Items.Count} items");

            Dictionary<int, List<string>> playersItems = new Dictionary<int, List<string>>();
            foreach ((int To, string Item) in message.Items)
                playersItems.GetOrCreateDefault(To).Add(Item);

            foreach (int playerId in playersItems.Keys)
                GameSessions[sender.Session.randoId].SendItemsTo(playerId, playersItems[playerId], sender.Session.playerId);

            // Confirm receiving a list of size to the sender
            SendMessage(new MWItemsSendConfirmMessage { ItemsCount = message.Items.Count }, sender);
        }

        private void HandleAnnounceCharmNotchCostsMessage(Client sender, MWAnnounceCharmNotchCostsMessage message)
        {
            sender.Session.ConfirmMessage(message);
            GameSessions[sender.Session.randoId].AnnouncePlayerCharmNotchCosts(sender.Session.playerId, message);
        }

        private void HandleConfirmCharmNotchCostsReceivedMessage(Client sender, MWConfirmCharmNotchCostsReceivedMessage message)
        {
            sender.Session.ConfirmMessage(message);
        }

        // Strictly ItemSync functions
        private void HandleVisitStateChangedMessage(Client sender, MWVisitStateChangedMessage message)
        {
            GameSessions[sender.Session.randoId].SendVisitStateChange(message, sender.Session.playerId);
        }

        private void HandleTransitionFoundMessage(Client sender, MWTransitionFoundMessage message)
        {
            GameSessions[sender.Session.randoId].SendTransitionFound(message.Source, message.Target, sender.Session.playerId);
        }

        // This has to be moved, but it will affect the entire file (as it should)

        internal void AddPushMessage(ulong playerId, MWMessage msg)
        {
            ThreadPool.QueueUserWorkItem(PushMessage, (playerId, msg));
        }

        internal void AddPushMessage(ulong playerId, MWConfirmableMessage msg)
        {
            AddPushMessage(playerId, (MWMessage)msg);
            Clients[playerId]?.Session?.QueueConfirmableMessage(msg);
        }

        private void PushMessage(object stateInfo)
        {
            (ulong playerId, MWMessage msg) = ((ulong, MWMessage))stateInfo;
            SendMessage(msg, Clients[playerId]);
        }
    }
}

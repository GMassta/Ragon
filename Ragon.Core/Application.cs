using System.Diagnostics;
using NLog;
using Ragon.Common;
using Ragon.Core.Lobby;
using Ragon.Core.Time;
using Ragon.Server;
using Ragon.Server.ENet;

namespace Ragon.Core;

public class Application : INetworkListener
{
  private Logger _logger = LogManager.GetCurrentClassLogger();
  
  private INetworkServer _server;
  private Thread _dedicatedThread;
  private Configuration _configuration;
  private HandlerRegistry _handlerRegistry;
  private ILobby _lobby;
  private Scheduler _scheduler;
  private Dictionary<ushort, PlayerContext> _contexts = new();

  public Application(Configuration configuration)
  {
    _configuration = configuration;
    _dedicatedThread = new Thread(Execute);
    _dedicatedThread.IsBackground = true;

    _handlerRegistry = new HandlerRegistry();
    _lobby = new LobbyInMemory();
    _scheduler = new Scheduler();

    if (configuration.Socket == "enet")
      _server = new ENetServer();

    Debug.Assert(_server != null, $"Socket type not supported: {configuration.Socket}. Supported: [enet, websocket]");
  }

  public void Execute()
  {
    while (true)
    {
      _scheduler.Tick();
      _server.Poll();
      
      Thread.Sleep((int) 1000.0f / _configuration.SendRate);
    }
  }

  public void Start()
  {
    var networkConfiguration = new NetworkConfiguration()
    {
      LimitConnections = _configuration.LimitConnections,
      Protocol = RagonVersion.Parse(_configuration.Protocol),
      Address = "0.0.0.0",
      Port = _configuration.Port,
    };

    _server.Start(this, networkConfiguration);
    _dedicatedThread.Start();
  }

  public void Stop()
  {
    _server.Stop();
    _dedicatedThread.Interrupt();
  }

  public void OnConnected(INetworkConnection connection)
  {
    var context = new PlayerContext(connection, new LobbyPlayer(connection));
    context.Lobby = _lobby;
    context.Scheduler = _scheduler;

    _logger.Trace($"Connected {connection.Id}");
    _contexts.Add(connection.Id, context);
  }

  public void OnDisconnected(INetworkConnection connection)
  {
    _logger.Trace($"Disconnected {connection.Id}");

    if (_contexts.Remove(connection.Id, out var context))
    {
      context.Room?.RemovePlayer(context.RoomPlayer);
      context.Dispose();
    }
  }

  public void OnTimeout(INetworkConnection connection)
  {
    if (_contexts.Remove(connection.Id, out var context))
      context.Dispose();
  }

  public void OnData(INetworkConnection connection, byte[] data)
  {
    if (_contexts.TryGetValue(connection.Id, out var context))
      _handlerRegistry.Handle(context, data);
  }
}
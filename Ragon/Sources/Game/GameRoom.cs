﻿using System;
using System.Collections.Generic;

using System.Linq;
using NLog;
using Ragon.Common;

namespace Ragon.Core
{
  public class GameRoom : IGameRoom
  {
    public int PlayersMin { get; private set; }
    public int PlayersMax { get; private set; }
    public int PlayersCount => _players.Count;
    public string Id { get; private set; }
    public string Map { get; private set; }

    private ILogger _logger = LogManager.GetCurrentClassLogger();
    private Dictionary<uint, Player> _players = new();
    private Dictionary<int, Entity> _entities = new();
    private uint _owner;

    private readonly PluginBase _plugin;
    private readonly IGameThread _gameThread;
    private readonly RagonSerializer _serializer = new(512);

    // Cache
    private uint[] _readyPlayers = Array.Empty<uint>();
    private uint[] _allPlayers = Array.Empty<uint>();
    private Entity[] _entitiesAll = Array.Empty<Entity>();
    
    public GameRoom(IGameThread gameThread, PluginBase pluginBase, string map, int min, int max)
    {
      _gameThread = gameThread;
      _plugin = pluginBase;

      Map = map;
      PlayersMin = min;
      PlayersMax = max;
      Id = Guid.NewGuid().ToString();

      _plugin.Attach(this);
    }

    public void Joined(Player player, ReadOnlySpan<byte> payload)
    {
      if (_players.Count == 0)
      {
        _owner = player.PeerId;
      }
      
      {
        _serializer.Clear();
        _serializer.WriteOperation(RagonOperation.PLAYER_JOINED);
        _serializer.WriteUShort((ushort) player.PeerId);
        _serializer.WriteString(player.Id);
        _serializer.WriteString(player.PlayerName);

        var sendData = _serializer.ToArray();
        Broadcast(_readyPlayers, sendData, DeliveryType.Reliable);
      }
      
      _players.Add(player.PeerId, player);
      _allPlayers = _players.Select(p => p.Key).ToArray();
      
      {
        _serializer.Clear();
        _serializer.WriteOperation(RagonOperation.JOIN_SUCCESS);
        _serializer.WriteString(Id);
        _serializer.WriteString(player.Id);
        _serializer.WriteString(GetOwner().Id);
        _serializer.WriteUShort((ushort) PlayersMin);
        _serializer.WriteUShort((ushort) PlayersMax);

        var sendData = _serializer.ToArray();
        Send(player.PeerId, sendData, DeliveryType.Reliable);
      }

      {
        _serializer.Clear();
        _serializer.WriteOperation(RagonOperation.LOAD_SCENE);
        _serializer.WriteString(Map);

        var sendData = _serializer.ToArray();
        Send(player.PeerId, sendData, DeliveryType.Reliable);
      }
    }

    public void Leave(uint peerId)
    {
      if (_players.Remove(peerId, out var player))
      {
        _allPlayers = _players.Select(p => p.Key).ToArray();
        _readyPlayers = _players.Where(p => p.Value.IsLoaded).Select(p => p.Key).ToArray();
        
        var isOwnershipChange = player.PeerId == _owner;

        {
          _plugin.OnPlayerLeaved(player);

          _serializer.Clear();
          _serializer.WriteOperation(RagonOperation.PLAYER_LEAVED);
          _serializer.WriteString(player.Id);

          _serializer.WriteUShort((ushort) player.EntitiesIds.Count);
          foreach (var entityId in player.EntitiesIds)
          {
            _serializer.WriteInt(entityId);
            _entities.Remove(entityId);
          }

          var sendData = _serializer.ToArray();
          Broadcast(_readyPlayers, sendData);
        }

        if (_allPlayers.Length > 0 && isOwnershipChange)
        {
          var newRoomOwnerId = _allPlayers[0];
          var newRoomOwner = _players[newRoomOwnerId];
          
          _owner = newRoomOwnerId;
          
          {
            _plugin.OnOwnershipChanged(newRoomOwner);

            _serializer.Clear();
            _serializer.WriteOperation(RagonOperation.OWNERSHIP_CHANGED);
            _serializer.WriteString(newRoomOwner.Id);

            var sendData = _serializer.ToArray();
            Broadcast(_readyPlayers, sendData);
          }
        }

        _entitiesAll = _entities.Values.ToArray();
      }
    }

    public void ProcessEvent(uint peerId, ReadOnlySpan<byte> rawData)
    {
      var operation = (RagonOperation) rawData[0];
      var payloadRawData = rawData.Slice(1, rawData.Length - 1);
       
      _serializer.Clear();
      _serializer.FromSpan(ref payloadRawData);
      
      switch (operation)
      {
        case RagonOperation.REPLICATE_ENTITY_STATE:
        {
          var entityId = _serializer.ReadInt();
          if (_entities.TryGetValue(entityId, out var ent))
          {
            if (ent.State.Authority == RagonAuthority.OWNER_ONLY && ent.OwnerId != peerId)
              return;

            var entityStateData = _serializer.ReadData(_serializer.Size);
            ent.State.Write(ref entityStateData);
          }
          break;
        }
        case RagonOperation.REPLICATE_ENTITY_EVENT:
        {
          var evntId = _serializer.ReadUShort();
          var entityId = _serializer.ReadInt();

          if (!_entities.TryGetValue(entityId, out var ent))
            return;

          if (ent.Authority == RagonAuthority.OWNER_ONLY && ent.OwnerId != peerId)
            return;

          Span<byte> payloadRaw = stackalloc byte[_serializer.Size];
          var payloadData = _serializer.ReadData(_serializer.Size);
          payloadData.CopyTo(payloadRaw);
          
          ReadOnlySpan<byte> payload = payloadRaw;
          if (_plugin.InternalHandle(peerId, entityId, evntId, ref payload))
            return;

          _serializer.Clear();
          _serializer.WriteOperation(RagonOperation.REPLICATE_ENTITY_EVENT);
          _serializer.WriteUShort(evntId);
          _serializer.WriteInt(entityId);
          _serializer.WriteData(ref payload);
          var sendData = _serializer.ToArray();
          
          Broadcast(_readyPlayers, sendData, DeliveryType.Reliable);
          break;
        }
        case RagonOperation.REPLICATE_EVENT:
        {
          var evntId = _serializer.ReadUShort();
          
          Span<byte> payloadRaw = stackalloc byte[_serializer.Size];
          var payloadData = _serializer.ReadData(_serializer.Size);
          payloadData.CopyTo(payloadRaw);
          
          ReadOnlySpan<byte> payload = payloadRaw;
          if (_plugin.InternalHandle(peerId, evntId, ref payload))
            return;

          _serializer.Clear();
          _serializer.WriteOperation(RagonOperation.REPLICATE_EVENT);
          _serializer.WriteUShort(evntId);
          _serializer.WriteData(ref payload);
          
          var sendData = _serializer.ToArray();
          Broadcast(_readyPlayers, sendData, DeliveryType.Reliable);
          break;
        }
        case RagonOperation.CREATE_ENTITY:
        {
          var entityType = _serializer.ReadUShort();
          var stateAuthority = (RagonAuthority) _serializer.ReadByte();
          var eventAuthority = (RagonAuthority) _serializer.ReadByte();
          var entity = new Entity(peerId, entityType, stateAuthority, eventAuthority);
          
          {
            var entityPayload = _serializer.ReadData(_serializer.Size);
            entity.Payload.Write(ref entityPayload);
          }

          var player = _players[peerId];
          player.Entities.Add(entity);
          player.EntitiesIds.Add(entity.EntityId);

          var ownerId = (ushort) peerId;

          _entities.Add(entity.EntityId, entity);
          _entitiesAll = _entities.Values.ToArray();

          _plugin.OnEntityCreated(player, entity);

          _serializer.Clear();
          _serializer.WriteOperation(RagonOperation.CREATE_ENTITY);
          _serializer.WriteUShort(entityType);
          _serializer.WriteByte((byte) stateAuthority);
          _serializer.WriteByte((byte) eventAuthority);
          _serializer.WriteInt(entity.EntityId);
          _serializer.WriteUShort(ownerId);
          
          {
            var entityPayload = entity.Payload.Read();
            _serializer.WriteData(ref entityPayload);
          }
          
          var sendData = _serializer.ToArray();
          Broadcast(_readyPlayers, sendData, DeliveryType.Reliable);
          break;
        }
        case RagonOperation.DESTROY_ENTITY:
        {
          var entityId = _serializer.ReadInt();
          if (_entities.TryGetValue(entityId, out var entity))
          {
            if (entity.Authority == RagonAuthority.OWNER_ONLY && entity.OwnerId != peerId)
              return;

            var player = _players[peerId];
            var destroyPayload = _serializer.ReadData(_serializer.Size);

            player.Entities.Remove(entity);
            player.EntitiesIds.Remove(entity.EntityId);

            _entities.Remove(entityId);
            _entitiesAll = _entities.Values.ToArray();

            _plugin.OnEntityDestroyed(player, entity);

            _serializer.Clear();
            _serializer.WriteOperation(RagonOperation.DESTROY_ENTITY);
            _serializer.WriteInt(entityId);
            _serializer.WriteData(ref destroyPayload);

            var sendData = _serializer.ToArray();
            Broadcast(_readyPlayers, sendData, DeliveryType.Reliable);
          }

          break;
        }
        case RagonOperation.SCENE_IS_LOADED:
        {
          _serializer.Clear();
          _serializer.WriteOperation(RagonOperation.SNAPSHOT);

          _serializer.WriteInt(_allPlayers.Length);
          foreach (var playerPeerId in _allPlayers)
          {
            _serializer.WriteString(_players[playerPeerId].Id);
            _serializer.WriteUShort((ushort) playerPeerId);
            _serializer.WriteString(_players[playerPeerId].PlayerName);
          }
          
          _serializer.WriteInt(_entitiesAll.Length);
          foreach (var entity in _entitiesAll)
          {
            var payload = entity.Payload.Read();
            var state = entity.State.Read();
            
            _serializer.WriteInt(entity.EntityId);
            _serializer.WriteByte((byte) entity.State.Authority);
            _serializer.WriteByte((byte) entity.Authority);
            _serializer.WriteUShort(entity.EntityType);
            _serializer.WriteUShort((ushort) entity.OwnerId);
            _serializer.WriteUShort((ushort) payload.Length);
            _serializer.WriteData(ref payload);
            _serializer.WriteUShort((ushort) state.Length);
            _serializer.WriteData(ref state);
          }

          var sendData = _serializer.ToArray();
          Send(peerId, sendData, DeliveryType.Reliable);

          _players[peerId].IsLoaded = true;
          _readyPlayers = _players.Where(p => p.Value.IsLoaded).Select(p => p.Key).ToArray();

          _plugin.OnPlayerJoined(_players[peerId]);
          break;
        }
      }
    }
    
    public void Tick(float deltaTime)
    {
      _plugin.OnTick(deltaTime);

      foreach (var entity in _entitiesAll)
      {
        if (entity.State.isDirty)
        {
          var state = entity.State.Read();

          _serializer.Clear();
          _serializer.WriteOperation(RagonOperation.REPLICATE_ENTITY_STATE);
          _serializer.WriteInt(entity.EntityId);
          _serializer.WriteData(ref state);

          var sendData = _serializer.ToArray();
          Broadcast(_readyPlayers, sendData, DeliveryType.Unreliable);

          entity.State.Clear();
        }
      }
    }

    public void Start()
    {
      _plugin.OnStart();
    }

    public void Stop()
    {
      _plugin.OnStop();
      _plugin.Detach();
    }

    public Player GetPlayerById(uint peerId) => _players[peerId];

    public Entity GetEntityById(int entityId) => _entities[entityId];
    
    public Player GetOwner() => _players[_owner];
    
    public IDispatcher GetThreadDispatcher() =>  _gameThread.ThreadDispatcher;

    public void Send(uint peerId, byte[] rawData, DeliveryType deliveryType = DeliveryType.Unreliable)
    {
      _gameThread.Server.Send(peerId, rawData, deliveryType);
    }

    public void Broadcast(uint[] peersIds, byte[] rawData, DeliveryType deliveryType = DeliveryType.Unreliable)
    {
      foreach (var peer in peersIds)
      {
        _gameThread.Server.Send(peer, rawData, deliveryType);
      }
    }

    public void Broadcast(byte[] rawData, DeliveryType deliveryType = DeliveryType.Unreliable)
    {
      foreach (var peer in _allPlayers)
      {
        _gameThread.Server.Send(peer, rawData, deliveryType);
      }
    }
  }
}
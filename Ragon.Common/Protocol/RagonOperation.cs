namespace Ragon.Common
{
  public enum RagonOperation: byte
  {
    AUTHORIZE,
    AUTHORIZED_SUCCESS,
    AUTHORIZED_FAILED,
    
    JOIN_OR_CREATE_ROOM,
    JOIN_ROOM,
    LEAVE_ROOM,
    OWNERSHIP_CHANGED,
    JOIN_SUCCESS,
    JOIN_FAILED,
    
    LOAD_SCENE,
    SCENE_IS_LOADED,

    PLAYER_JOINED,
    PLAYER_LEAVED,
    
    CREATE_ENTITY,
    CREATE_STATIC_ENTITY,
    DESTROY_ENTITY,
    
    SNAPSHOT,
    
    REPLICATE_ENTITY_STATE,
    REPLICATE_ENTITY_EVENT,
    REPLICATE_EVENT,
  }
}
using Akka.Actor;

namespace DexaBridge
{
    /// <summary>
    /// CommProxy.Initialize의 listenerType으로 사용하는 더미 actor.
    /// 서버의 DEXAssoiciationEventListener 대체.
    /// </summary>
    public class DummyListenerActor : UntypedActor
    {
        protected override void OnReceive(object message) { }
    }
}

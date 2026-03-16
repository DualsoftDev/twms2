namespace DEX.Core.Actor

type AmInjectSubscription(subscription: AmSubscribe) =
    inherit ActorMessage()
    member val Subscription: AmSubscribe = subscription with get, set

    new() = AmInjectSubscription(null)

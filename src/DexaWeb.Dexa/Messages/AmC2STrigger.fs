namespace DEX.Core.Actor

open DEX.Core.Database.ORM

[<AbstractClass>]
type AmC2STrigger(trigger: Trigger) =
    inherit ActorMessage()
    member val Trigger: Trigger = trigger with get, set

    new() = AmC2STrigger(null)

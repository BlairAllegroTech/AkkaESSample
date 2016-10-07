using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Akka.Actor;
using Akka.Event;
using Akka.Routing;
using Akka.DI.Core;

using Akka.EventStore.Cqrs.Core;
using Messages;
using Actors;

namespace Grotto.Core.Actor.Actors
{
    public class AggregateRootCoordinatorActor : ReceiveActor
    {
        private Dictionary<Guid,IActorRef> _accountWorkerRefs = new Dictionary<Guid,IActorRef>();
        private ILoggingAdapter _log;

        public AggregateRootCoordinatorActor()
        {
            _log = Context.GetLogger();
            Ready();
        }

        protected override void PreStart()
        {
            _log.Debug("AggregateRootCoordinatorActor:PreStart");
            //this will guarantee that our messages go to the specific operator based on the hash-id
            //_accountWorkerPool = Context.ActorOf(Context.DI().Props<AccountActor>()
            //    .WithRouter(new ConsistentHashingPool(10)));
        }

        protected override void PreRestart(Exception reason, object message)
        {
            _log.Error(reason, "Error AggregateRootCoordinatorActor:PreRestart about to restart");
        }

        protected override void PostRestart(Exception reason)
        {
            _log.Error(reason, "Error AggregateRootCoordinatorActor:PostRestart restarted");
        }

        private void Ready()
        {
            _log.Debug("AggregateRootCoordinatorActor entering Ready state");
            Receive<IAccountMessage>(msg =>
            {
                
                var aggregatePath = this.Self.Path.Child("aggregates(account)" + msg.AccountId.ToString());
                //Context.ActorSelection("aggregates(account)" + msg.AccountId.ToString()).Tell(msg);
                //Context.ActorSelection(aggregatePath).Tell(msg);

                //this is a pool of operators and we want to forward our message on to a PropertyOperator
                if (!_accountWorkerRefs.ContainsKey(msg.AccountId))
                {
                    //_accountWorkerRefs.Add(msg.AccountId,Context.ActorOf(Context.DI().Props<AccountActor>()));
                    var parms = new AggregateRootCreationParameters(
                        msg.AccountId, 
                        ActorRefs.Nobody, 
                        snapshotThreshold:100,
                        receiveTimeout: TimeSpan.FromMinutes(2)
                        );

                    var props = Props.Create<AccountActor>(parms);
                    _accountWorkerRefs.Add(msg.AccountId, Context.ActorOf(props, "aggregates(account)" + msg.AccountId.ToString()));

                    _log.Debug("Account:{0}, Added to Agg Root Coordinator Cache", msg.AccountId);
                }
                _accountWorkerRefs[msg.AccountId].Forward(msg);
            });

            Receive<Akka.EventStore.Cqrs.Core.Messages.PassivateMessage>(msg => 
            {
                _log.Debug("Account:{0}, timed out, due to no activity", msg.Id);
                var actorToUnload = Context.Sender;
                actorToUnload.GracefulStop(TimeSpan.FromSeconds(10)).ContinueWith((success) => 
                {
                    if(success.Result)
                    {
                        _accountWorkerRefs.Remove(msg.Id);
                        _log.Debug("Account:{0}, removed", msg.Id);
                    }
                    else
                    {
                        _log.Warning("Account:{0}, failed to removed", msg.Id);
                    }
                });

                // the time between the above and below lines, we need to intercept messages to the child that is being
                // removed from memory - how to do this?

                //task.Wait(); // dont block thread, use pipeto instead?
            });
        }

        protected override SupervisorStrategy SupervisorStrategy()
        {
            return new OneForOneStrategy(
                maxNrOfRetries: 10,
                withinTimeRange: TimeSpan.FromSeconds(30),
                localOnlyDecider: x =>
                {
                    // Error that we have no idea what to do with
                    //else if (x is InsanelyBadException) return Directive.Escalate;

                    // Error that we can't recover from, stop the failing child
                    if (x is NotSupportedException) return Directive.Stop;

                    // otherwise restart the failing child
                    else return Directive.Restart;
                });
        }
    }
}

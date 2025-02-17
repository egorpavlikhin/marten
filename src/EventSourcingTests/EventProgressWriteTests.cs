using Baseline;
using Marten.Events;
using Marten.Events.Operations;
using Marten.Testing.Harness;
using Shouldly;
using Weasel.Postgresql;
using Xunit;

namespace EventSourcingTests
{
    public class EventProgressWriteTests: IntegrationContext
    {
        public EventProgressWriteTests(DefaultStoreFixture fixture) : base(fixture)
        {
            theStore.Tenancy.Default.Database.EnsureStorageExists(typeof(StreamAction));
        }

        [Fact]
        public void can_register_progress_initial()
        {
            using (var session = theStore.OpenSession())
            {
                var events = theStore.Events;
                session.QueueOperation(new EventProgressWrite(events, "summary", 111));
                session.SaveChanges();

                var last =
                    session.Connection.CreateCommand(
                        "select last_seq_id from mt_event_progression where name = 'summary'")
                        .ExecuteScalar().As<long>();

                last.ShouldBe(111);
            }
        }

        [Fact]
        public void can_register_subsequent_progress()
        {
            using (var session = theStore.OpenSession())
            {
                var events = theStore.Events;

                session.QueueOperation(new EventProgressWrite(events, "summary", 111));
                session.SaveChanges();

                session.QueueOperation(new EventProgressWrite(events, "summary", 222));
                session.SaveChanges();

                var last =
                    session.Connection.CreateCommand(
                        "select last_seq_id from mt_event_progression where name = 'summary'")
                        .ExecuteScalar().As<long>();

                last.ShouldBe(222);
            }
        }
    }
}

using System;
using SetNet.Config;
using SetNet.Core;
using SetNet.Services;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>Covers the <see cref="ServiceHub"/> locator: add/resolve by type, the ambient hub + <see cref="Service"/> facade, and per-server isolation.</summary>
    public class ServicesTests
    {
        private sealed class Foo { public int N; }
        private sealed class Bar { }

        [Fact]
        public void Add_returns_the_instance_and_Get_resolves_it()
        {
            var hub = new ServiceHub();
            var foo = new Foo { N = 7 };
            Assert.Same(foo, hub.Add(foo));     // Add returns it for inline capture
            Assert.Same(foo, hub.Get<Foo>());
            Assert.Equal(7, hub.Get<Foo>().N);
        }

        [Fact]
        public void Get_throws_when_missing_TryGet_does_not()
        {
            var hub = new ServiceHub();
            Assert.Throws<InvalidOperationException>(() => hub.Get<Foo>());
            Assert.False(hub.TryGet<Foo>(out _));
            Assert.Null(hub.GetOrNull<Foo>());
        }

        [Fact]
        public void Re_adding_replaces()
        {
            var hub = new ServiceHub();
            var a = new Foo();
            var b = new Foo();
            hub.Add(a);
            hub.Add(b);
            Assert.Same(b, hub.Get<Foo>());
        }

        [Fact]
        public void Ambient_hub_backs_the_Service_facade()
        {
            var prev = ServiceHub.Current;
            try
            {
                var hub = new ServiceHub().MakeCurrent();
                Assert.Same(hub, ServiceHub.Current);
                var bar = hub.Add(new Bar());
                Assert.Same(bar, Service.Get<Bar>());
                Assert.True(Service.TryGet<Bar>(out _));
                Assert.False(Service.TryGet<Foo>(out _));
            }
            finally { ServiceHub.Current = prev; }
        }

        [Fact]
        public void Per_server_hubs_are_isolated()
        {
            var s1 = new TinyServer();
            var s2 = new TinyServer();
            var foo = s1.Services().Add(new Foo { N = 1 });

            Assert.Same(foo, s1.Services().Get<Foo>());   // same hub returned for the same server
            Assert.False(s2.Services().TryGet<Foo>(out _)); // a different server has a different hub
        }

        private sealed class TinyServer : BaseServer
        {
            public TinyServer() : base(new Configuration()) { }
            protected override BasePeer OnNewClient(PeerInfo peerInfo) => new TinyPeer(peerInfo);
        }

        private sealed class TinyPeer : BasePeer
        {
            public TinyPeer(PeerInfo peerInfo) : base(peerInfo) { }
            protected override void OnDisconnected() { }
        }
    }
}

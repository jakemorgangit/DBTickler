using DBTickler.Core.Observability;
using DBTickler.Core.Tests.Testing;

namespace DBTickler.Core.Tests.Observability;

public class BlockingAnalyzerTests
{
    [Fact]
    public void Empty_input_produces_empty_output() =>
        Assert.Empty(BlockingAnalyzer.BuildChains([]));

    [Fact]
    public void Null_input_throws() =>
        Assert.Throws<ArgumentNullException>(() => BlockingAnalyzer.BuildChains(null!));

    [Fact]
    public void Requests_with_no_blocking_at_all_produce_no_chains()
    {
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 1),
            ActiveRequestFactory.Create(sessionId: 2),
        };

        Assert.Empty(BlockingAnalyzer.BuildChains(requests));
    }

    [Fact]
    public void Simple_chain_produces_one_root_with_correct_nesting()
    {
        // A <- B <- C : session 2 is blocked by 1, session 3 is blocked by 2.
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 1),
            ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1),
            ActiveRequestFactory.Create(sessionId: 3, blockingSessionId: 2),
        };

        var roots = BlockingAnalyzer.BuildChains(requests);

        var root = Assert.Single(roots);
        Assert.Equal(1, root.Request.SessionId);
        var child = Assert.Single(root.Blocked);
        Assert.Equal(2, child.Request.SessionId);
        var grandchild = Assert.Single(child.Blocked);
        Assert.Equal(3, grandchild.Request.SessionId);
        Assert.Empty(grandchild.Blocked);
    }

    [Fact]
    public void Multiple_independent_chains_all_appear_as_separate_roots()
    {
        var requests = new[]
        {
            // Chain 1: root 1 blocks 2 (one session blocked below it).
            ActiveRequestFactory.Create(sessionId: 1),
            ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1),

            // Chain 2: root 10 blocks 11 and 12 (two sessions blocked below it).
            ActiveRequestFactory.Create(sessionId: 10),
            ActiveRequestFactory.Create(sessionId: 11, blockingSessionId: 10),
            ActiveRequestFactory.Create(sessionId: 12, blockingSessionId: 10),
        };

        var roots = BlockingAnalyzer.BuildChains(requests);

        Assert.Equal(2, roots.Count);
        // Ordered by TotalBlockedBelow descending, so the busier chain (root 10, 2 blocked) comes first.
        Assert.Equal(10, roots[0].Request.SessionId);
        Assert.Equal(2, roots[0].TotalBlockedBelow);
        Assert.Equal(1, roots[1].Request.SessionId);
        Assert.Equal(1, roots[1].TotalBlockedBelow);
    }

    [Fact]
    public void Head_blocker_with_no_request_row_of_its_own_still_becomes_a_root()
    {
        // Session 1 is a head blocker (sleeping with an open transaction): it never shows up
        // as a row of its own, only as somebody else's BlockingSessionId.
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1),
        };

        var roots = BlockingAnalyzer.BuildChains(requests);

        var root = Assert.Single(roots);
        Assert.Equal(1, root.Request.SessionId);
        Assert.Equal("sleeping", root.Request.Status);
        Assert.Equal("(idle with open transaction)", root.Request.Command);
        Assert.Null(root.Request.BlockingSessionId);
        var child = Assert.Single(root.Blocked);
        Assert.Equal(2, child.Request.SessionId);
    }

    [Fact]
    public void A_pure_mutual_cycle_is_still_reported_and_terminates()
    {
        // 1 is blocked by 2, and 2 is blocked by 1: an instantaneous snapshot of a deadlock
        // about to be resolved. No session qualifies as a head blocker, but the cycle must
        // still appear — it would otherwise vanish from the display at exactly the moment it
        // is most interesting. BuildChains must also return rather than recursing forever.
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 1, blockingSessionId: 2, waitTimeMs: 500),
            ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1, waitTimeMs: 900),
        };

        var roots = BlockingAnalyzer.BuildChains(requests);

        var root = Assert.Single(roots);
        // Rooted at the longest waiter, so the display leads with the worst-affected session.
        Assert.Equal(2, root.Request.SessionId);
        Assert.Equal(1, root.TotalBlockedBelow);
    }

    [Fact]
    public void A_pure_three_way_cycle_is_reported_once_and_terminates()
    {
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 1, blockingSessionId: 3, waitTimeMs: 100),
            ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1, waitTimeMs: 200),
            ActiveRequestFactory.Create(sessionId: 3, blockingSessionId: 2, waitTimeMs: 300),
        };

        var roots = BlockingAnalyzer.BuildChains(requests);

        var root = Assert.Single(roots);
        Assert.Equal(3, root.Request.SessionId);

        // Every participant appears exactly once: the recursion guard stops the ring from
        // being walked more than once round.
        Assert.Equal(2, root.TotalBlockedBelow);
    }

    [Fact]
    public void A_cycle_reachable_below_a_genuine_root_is_cut_off_by_the_recursion_guard()
    {
        // Simulates a corrupted/duplicated snapshot: two distinct rows both claim session id 2
        // (SQL Server itself would never produce this, but the analyzer is a pure function
        // over whatever list it is handed, and must not infinite-loop on bad input). The
        // second "session 2" row closes a cycle back to the first one two levels down.
        //
        //   1 (unblocked, real root)
        //    └─ 2  (blocked by 1)
        //        └─ 3  (blocked by 2)
        //            └─ 2' (blocked by 3) -- same session id as the node two levels up
        var head = ActiveRequestFactory.Create(sessionId: 1);
        var a = ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1);
        var b = ActiveRequestFactory.Create(sessionId: 3, blockingSessionId: 2);
        var cycleBack = ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 3);

        var roots = BlockingAnalyzer.BuildChains([head, a, b, cycleBack]);

        var root = Assert.Single(roots);
        Assert.Equal(1, root.Request.SessionId);

        var levelA = Assert.Single(root.Blocked);
        Assert.Equal(2, levelA.Request.SessionId);

        var levelB = Assert.Single(levelA.Blocked);
        Assert.Equal(3, levelB.Request.SessionId);

        // The guard fires here: session 2 is already on the path, so the walk stops at the
        // closing edge rather than looping back through 3 forever. The repeat is dropped
        // rather than emitted, which keeps each session counted once — TotalBlockedBelow is
        // shown to the operator as "sessions waiting behind this one", and a session appearing
        // twice would inflate it.
        Assert.Empty(levelB.Blocked);
        Assert.Equal(2, root.TotalBlockedBelow);
    }

    [Fact]
    public void Self_blocking_rows_are_ignored()
    {
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 1, blockingSessionId: 1),
        };

        Assert.Empty(BlockingAnalyzer.BuildChains(requests));
    }

    [Fact]
    public void Children_under_a_root_are_ordered_by_wait_time_descending()
    {
        var requests = new[]
        {
            ActiveRequestFactory.Create(sessionId: 1),
            ActiveRequestFactory.Create(sessionId: 2, blockingSessionId: 1, waitTimeMs: 100),
            ActiveRequestFactory.Create(sessionId: 3, blockingSessionId: 1, waitTimeMs: 9000),
            ActiveRequestFactory.Create(sessionId: 4, blockingSessionId: 1, waitTimeMs: 500),
        };

        var root = Assert.Single(BlockingAnalyzer.BuildChains(requests));

        Assert.Equal([3, 4, 2], root.Blocked.Select(node => node.Request.SessionId));
    }

    public class BlockingNodeMath
    {
        [Fact]
        public void TotalBlockedBelow_counts_every_descendant_at_any_depth()
        {
            var leaf = new BlockingNode(ActiveRequestFactory.Create(4), []);
            var mid = new BlockingNode(ActiveRequestFactory.Create(2), [leaf]);
            var sibling = new BlockingNode(ActiveRequestFactory.Create(3), []);
            var root = new BlockingNode(ActiveRequestFactory.Create(1), [mid, sibling]);

            Assert.Equal(3, root.TotalBlockedBelow); // 2, 3, and 4
            Assert.Equal(1, mid.TotalBlockedBelow);  // 4
            Assert.Equal(0, leaf.TotalBlockedBelow);
        }

        [Fact]
        public void MaxWaitMsBelow_is_the_longest_wait_anywhere_in_the_subtree()
        {
            var grandchild = new BlockingNode(ActiveRequestFactory.Create(4, waitTimeMs: 300), []);
            var child2 = new BlockingNode(ActiveRequestFactory.Create(2, waitTimeMs: 100), [grandchild]);
            var child3 = new BlockingNode(ActiveRequestFactory.Create(3, waitTimeMs: 200), []);
            var root = new BlockingNode(ActiveRequestFactory.Create(1, waitTimeMs: 0), [child2, child3]);

            Assert.Equal(300, root.MaxWaitMsBelow); // deepest wait wins, not the immediate child's own wait
            Assert.Equal(300, child2.MaxWaitMsBelow);
            Assert.Equal(0, grandchild.MaxWaitMsBelow); // no children
        }

        [Fact]
        public void MaxWaitMsBelow_is_zero_for_a_node_with_no_blocked_children()
        {
            var leaf = new BlockingNode(ActiveRequestFactory.Create(1, waitTimeMs: 5000), []);
            Assert.Equal(0, leaf.MaxWaitMsBelow);
        }
    }

    public class ActiveRequestProperties
    {
        [Fact]
        public void IsBlocked_is_true_only_for_a_positive_blocking_session_id()
        {
            Assert.True(ActiveRequestFactory.Create(1, blockingSessionId: 5).IsBlocked);
            Assert.False(ActiveRequestFactory.Create(1, blockingSessionId: null).IsBlocked);
            Assert.False(ActiveRequestFactory.Create(1, blockingSessionId: 0).IsBlocked);
        }

        [Fact]
        public void IsOurs_matches_the_DBTickler_application_name_exactly()
        {
            Assert.True(ActiveRequestFactory.Create(1, programName: "DBTickler").IsOurs);
            Assert.False(ActiveRequestFactory.Create(1, programName: "SSMS").IsOurs);
            Assert.False(ActiveRequestFactory.Create(1, programName: null).IsOurs);
        }
    }
}

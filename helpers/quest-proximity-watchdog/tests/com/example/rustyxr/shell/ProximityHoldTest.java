package com.example.rustyxr.shell;

public final class ProximityHoldTest {
    private static int cases;

    public static void main(String[] args) throws Exception {
        unchangedCloseRenews();
        shortHoldAndFailure();
        stopAndUnknownReadback();
        repairAndClockRange();
        finiteSessionAndCancellation();
        exceptionDoesNotRenew();
        System.out.println("PASS " + cases + " proximity hold cases");
    }

    // A CLOSE-only branch would suppress both establishment and later renewal.
    private static void unchangedCloseRenews() throws Exception {
        ProximityHold hold = new ProximityHold(28800000, 5000, 25000);
        Fake env = new Fake();
        check(hold.poll(true, "CLOSE", env) != null, "unknown CLOSE establishes local hold");
        check(env.calls == 1 && env.duration == 28800000, "one finite initial hold");
        for (long time : new long[] {0, 5000, 21600000 - 1}) {
            env.now = time;
            check(hold.poll(true, "CLOSE", env) == null, "no premature renewal at " + time);
        }
        env.now = 21600000;
        check(hold.poll(true, "CLOSE", env).success, "renew at six hours before eight-hour expiry");
        check(env.calls == 2, "one renewal");
        check(hold.poll(true, "CLOSE", env) == null && env.calls == 2, "same-time poll never duplicates");
        cases++;
    }

    // Failed commands must not manufacture a new coverage deadline or tight retry loop.
    private static void shortHoldAndFailure() throws Exception {
        ProximityHold hold = new ProximityHold(1000, 100, 20);
        Fake env = new Fake();
        hold.poll(true, "CLOSE", env);
        env.now = 750;
        env.result = new ProximityHold.Result(false, "timeout");
        check(!hold.poll(true, "CLOSE", env).success, "failure returned");
        check(hold.lastSuccessElapsedMs == 0, "failure keeps last success");
        env.now = 849;
        check(hold.poll(true, "CLOSE", env) == null, "retry bounded by poll");
        env.now = 850;
        env.result = new ProximityHold.Result(true, "");
        check(hold.poll(true, "CLOSE", env).success, "retry recovers before old expiry");
        env.now = 1001;
        check(hold.poll(true, "CLOSE", env) == null, "old expiry does not duplicate renewed hold");
        cases++;
    }

    // Stop may arrive during the due decision; unavailable readback grants no dispatch.
    private static void stopAndUnknownReadback() throws Exception {
        ProximityHold hold = new ProximityHold(1000, 100, 20);
        Fake env = new Fake();
        check(hold.poll(false, "", env) == null && env.calls == 0, "unknown readback blocks");
        env.stop = true;
        check(hold.poll(true, "FAR", env) == null && env.calls == 0, "stop before decision");
        env.stop = false;
        env.stopOnSecondCheck = true;
        check(hold.poll(true, "FAR", env) == null && env.calls == 0, "stop before dispatch");
        cases++;
    }

    // Preserve non-CLOSE repair, reject impossible timing, and avoid absolute-deadline overflow.
    private static void repairAndClockRange() throws Exception {
        ProximityHold hold = new ProximityHold(1000, 100, 20);
        Fake env = new Fake();
        env.now = Long.MAX_VALUE - 900;
        check(hold.poll(true, "FAR", env).success, "repair state");
        env.now += 749;
        check(hold.poll(true, "CLOSE", env) == null, "large monotonic time no premature renewal");
        env.now++;
        check(hold.poll(true, "CLOSE", env).success, "large monotonic time due");
        int invalid = 0;
        for (long[] timing : new long[][] {{0, 100, 20}, {100, 100, 20},
                {1000, 0, 20}, {Long.MAX_VALUE, 100, 20}, {1000, 100, -1}}) {
            try { new ProximityHold(timing[0], timing[1], timing[2]); }
            catch (IllegalArgumentException expected) { invalid++; }
        }
        check(invalid == 5, "invalid ranges rejected");
        cases++;
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    // A finite session must stop even if renewal became due during slow readback.
    private static void finiteSessionAndCancellation() throws Exception {
        check(!ProximityHold.sessionEnded(999, 0, 1000, false, false), "finite session before end");
        check(ProximityHold.sessionEnded(1000, 0, 1000, false, false), "finite session at end");
        check(!ProximityHold.sessionEnded(100000, 0, 1000, true, false), "until-stopped survives duration");
        check(ProximityHold.sessionEnded(100000, 0, 1000, true, true), "stop ends indefinite session");
        ProximityHold hold = new ProximityHold(1000, 100, 20);
        Fake env = new Fake();
        env.stop = ProximityHold.sessionEnded(1000, 0, 1000, false, false);
        check(hold.poll(true, "CLOSE", env) == null && env.calls == 0, "finite deadline prevents dispatch");
        env.stop = false;
        env.failure = new InterruptedException("cancel");
        try {
            hold.poll(true, "CLOSE", env);
            throw new AssertionError("cancellation swallowed");
        } catch (InterruptedException expected) {
            check(!hold.hasSuccess, "cancellation cannot establish coverage");
        }
        cases++;
    }

    // Thrown I/O errors must remain visible and obey the same bounded retry cadence.
    private static void exceptionDoesNotRenew() throws Exception {
        ProximityHold hold = new ProximityHold(1000, 100, 20);
        Fake env = new Fake();
        env.failure = new java.io.IOException("broadcast unavailable");
        ProximityHold.Result failed = hold.poll(true, "CLOSE", env);
        check(!failed.success && failed.error.contains("broadcast unavailable"), "exception preserved");
        check(!hold.hasSuccess, "exception grants no coverage");
        env.now = 99;
        check(hold.poll(true, "FAR", env) == null, "exception retry waits");
        env.now = 100;
        env.failure = null;
        env.commandElapsedMs = 10;
        check(hold.poll(true, "CLOSE", env).success, "exception recovery");
        check(hold.lastSuccessElapsedMs == 100 && env.now == 110, "success uses conservative dispatch time");
        cases++;
    }

    private static final class Fake implements ProximityHold.Environment {
        long now;
        long duration;
        int calls;
        int stopChecks;
        boolean stop;
        boolean stopOnSecondCheck;
        long commandElapsedMs;
        Exception failure;
        ProximityHold.Result result = new ProximityHold.Result(true, "");
        public long elapsedMs() { return now; }
        public boolean stopped() { return stop || (stopOnSecondCheck && ++stopChecks >= 2); }
        public ProximityHold.Result broadcast(long holdMs) throws Exception {
            duration = holdMs;
            calls++;
            if (failure != null) throw failure;
            now += commandElapsedMs;
            return result;
        }
    }
}

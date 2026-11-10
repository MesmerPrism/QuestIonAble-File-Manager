package com.example.rustyxr.shell;

public final class StandaloneWatchdogTest {
    public static void main(String[] args) throws Exception {
        StandaloneWatchdog.validateTiming(28800000, 5000, 28800000);
        check(new ProximityHold(28800000, 5000, 25000).renewAfterMs == 21600000, "six hour renewal");
        for (long[] invalid : new long[][] {{59999, 5000, 60000}, {28800001, 5000, 60000},
                {60000, 60000, 60000}, {60000, 999, 60000}, {28800000, 60001, 60000},
                {28800000, 5000, 0}}) {
            boolean rejected = false;
            try { StandaloneWatchdog.validateTiming(invalid[0], invalid[1], invalid[2]); }
            catch (IllegalArgumentException expected) { rejected = true; }
            check(rejected, "invalid timing must fail before lease");
        }
        check(!StandaloneWatchdog.shouldWake("", ""), "missing power fields confer no repair authority");
        check(!StandaloneWatchdog.shouldWake("Awake", "ON_SUSPEND"), "accepted suspended display");
        check(StandaloneWatchdog.shouldWake("Asleep", "OFF"), "observed drift requests wake");
        check("CLOSE".equals(StandaloneWatchdog.match(StandaloneWatchdog.PROXIMITY,
            "Virtual proximity state: CLOSE\nHeadset state: Awake")), "proximity parser");
        check("".equals(StandaloneWatchdog.match(StandaloneWatchdog.PROXIMITY,
            "vrpowermanager unavailable")), "unavailable proximity grants no broadcast");
        StringBuilder stat = new StringBuilder("42 (helper (with spaces)) S");
        for (int field = 4; field <= 21; field++) stat.append(" 0");
        stat.append(" 987654 0");
        check("987654".equals(StandaloneWatchdog.processStartTicks(stat.toString())), "PID start ticks tolerate comm spaces");
        check(!new StandaloneWatchdog.CommandCapture("", true, 0, false).success(), "timeout is not success");
        check(!new StandaloneWatchdog.CommandCapture("", false, 0, true).success(), "truncation is not readback");
        check("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad".equals(
            StandaloneWatchdog.sha256("abc".getBytes("UTF-8"))), "startup artifact hash");
        System.out.println("PASS standalone adapter timing, readback and process identity cases");
    }
    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }
}

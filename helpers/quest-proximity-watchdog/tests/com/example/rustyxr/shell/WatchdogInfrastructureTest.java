package com.example.rustyxr.shell;

import java.io.File;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.nio.file.Files;

public final class WatchdogInfrastructureTest {
    public static void main(String[] args) throws Exception {
        if (args.length > 0 && "sleep".equals(args[0])) {
            Thread.sleep(5000);
            return;
        }
        if (args.length > 0 && "lease".equals(args[0])) {
            try (ProximityWatchdogLease lease = ProximityWatchdogLease.acquire(new File(args[1]))) {
                if (lease != null) throw new AssertionError("duplicate child acquired watchdog");
            }
            return;
        }
        if (args.length > 0 && "signalSleep".equals(args[0])) {
            new File(args[1]).createNewFile();
            Thread.sleep(5000);
            return;
        }
        boundedCommandTimesOut();
        exclusiveWatchdogPreservesStop();
        interruptedCommandStopsSubsequentEffects();
        System.out.println("PASS 3 watchdog infrastructure cases");
    }

    // A deadline applied only after process exit would wait five seconds, not one.
    private static void boundedCommandTimesOut() throws Exception {
        Method run = StandaloneWatchdog.class.getDeclaredMethod("runBoundedCommand", String[].class, int.class, int.class);
        run.setAccessible(true);
        String java = new File(System.getProperty("java.home"), "bin/java.exe").getPath();
        long started = System.nanoTime();
        Object capture = run.invoke(null, new Object[] {new String[] {java, "-cp",
            System.getProperty("java.class.path"), WatchdogInfrastructureTest.class.getName(), "sleep"}, 1024, 1});
        long elapsedMs = (System.nanoTime() - started) / 1000000;
        Field timeout = capture.getClass().getDeclaredField("timedOut");
        timeout.setAccessible(true);
        check(timeout.getBoolean(capture), "silent running child must time out");
        check(elapsedMs < 4000, "timeout applies while child is running: " + elapsedMs);
    }

    // A duplicate may neither enter the watchdog nor clear its owner's stop marker.
    private static void exclusiveWatchdogPreservesStop() throws Exception {
        File root = Files.createTempDirectory("proximity-watchdog-test").toFile();
        File stop = new File(root, "watchdog.stop");
        File lock = new File(stop.getPath() + ".lock");
        try {
            stop.createNewFile();
            try (ProximityWatchdogLease first = ProximityWatchdogLease.acquire(stop)) {
                check(first != null && !stop.exists(), "first owner clears stale marker");
                stop.createNewFile();
                ProximityWatchdogLease duplicate = ProximityWatchdogLease.acquire(stop);
                check(duplicate == null, "duplicate cannot acquire same watchdog");
                check(stop.exists(), "duplicate preserves active stop request");
                String javaExe = new File(System.getProperty("java.home"), "bin/java.exe").getPath();
                Process child = new ProcessBuilder(javaExe, "-cp", System.getProperty("java.class.path"),
                    WatchdogInfrastructureTest.class.getName(), "lease", stop.getPath()).inheritIO().start();
                try {
                    check(child.waitFor(4, java.util.concurrent.TimeUnit.SECONDS), "duplicate child completes");
                    check(child.exitValue() == 0 && stop.exists(), "cross-process duplicate preserves stop");
                } finally { child.destroy(); }
            }
            try (ProximityWatchdogLease next = ProximityWatchdogLease.acquire(stop)) {
                check(next != null && !stop.exists(), "next owner can start after release");
            }
        } finally {
            Files.deleteIfExists(stop.toPath());
            Files.deleteIfExists(lock.toPath());
            Files.deleteIfExists(root.toPath());
        }
    }

    private static void check(boolean condition, String message) {
        if (!condition) throw new AssertionError(message);
    }

    // Reader catch(Exception) must not erase cancellation after a real command is interrupted.
    private static void interruptedCommandStopsSubsequentEffects() throws Exception {
        File root = Files.createTempDirectory("proximity-cancel-test").toFile();
        final File ready = new File(root, "child-ready");
        final java.util.concurrent.atomic.AtomicReference<Throwable> failure =
            new java.util.concurrent.atomic.AtomicReference<Throwable>();
        Thread worker = new Thread(new Runnable() {
            public void run() {
                try {
                    Method run = StandaloneWatchdog.class.getDeclaredMethod("runBoundedCommand", String[].class, int.class, int.class);
                    run.setAccessible(true);
                    String javaExe = new File(System.getProperty("java.home"), "bin/java.exe").getPath();
                    try {
                        run.invoke(null, new Object[] {new String[] {javaExe, "-cp",
                            System.getProperty("java.class.path"), WatchdogInfrastructureTest.class.getName(),
                            "signalSleep", ready.getPath()}, 1024, 5});
                        throw new AssertionError("interrupted command returned normally");
                    } catch (java.lang.reflect.InvocationTargetException expected) {
                        check(expected.getCause() instanceof InterruptedException, "actual command cancellation");
                    }
                    check(Thread.currentThread().isInterrupted(), "command must restore cancellation flag");
                    for (String reader : new String[] {"readPowerState", "readProximityState"}) {
                        Method read = StandaloneWatchdog.class.getDeclaredMethod(reader);
                        read.setAccessible(true);
                        Object readback = read.invoke(null);
                        Method available = readback.getClass().getDeclaredMethod("success");
                        available.setAccessible(true);
                        check(!((Boolean) available.invoke(readback)).booleanValue(), "cancelled read remains unavailable");
                        check(Thread.currentThread().isInterrupted(), "reader catch preserves cancellation");
                    }
                    ProximityHold hold = new ProximityHold(1000, 100, 20);
                    check(hold.poll(true, "CLOSE", new ProximityHold.Environment() {
                        public long elapsedMs() { return 0; }
                        public boolean stopped() { return Thread.currentThread().isInterrupted(); }
                        public ProximityHold.Result broadcast(long holdMs) {
                            throw new AssertionError("broadcast after command cancellation");
                        }
                    }) == null, "cancelled thread cannot broadcast after readers return");
                } catch (Throwable error) { failure.set(error); }
            }
        });
        try {
            worker.start();
            long started = System.nanoTime();
            while (!ready.exists() && worker.isAlive() && System.nanoTime() - started < 3000000000L) {
                Thread.sleep(10);
            }
            check(ready.exists(), "actual child started before cancellation");
            worker.interrupt();
            worker.join(4000);
            check(!worker.isAlive(), "cancelled worker terminates promptly");
            if (failure.get() != null) throw new AssertionError("cancellation integration", failure.get());
        } finally {
            worker.interrupt();
            worker.join(6000);
            Files.deleteIfExists(ready.toPath());
            Files.deleteIfExists(root.toPath());
        }
    }
}

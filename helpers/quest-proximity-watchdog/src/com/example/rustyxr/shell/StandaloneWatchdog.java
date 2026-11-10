package com.example.rustyxr.shell;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.channels.FileLock;
import java.nio.channels.OverlappingFileLockException;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.Properties;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;

/** Standalone shell-only adapter of the Rusty-XR finite proximity watchdog.
 * See UPSTREAM.md. The scheduler and lease are imported without modification. */
public final class StandaloneWatchdog {
    static final String ROOT = "/data/local/tmp/questionable-file-manager-proximity-watchdog";
    static final File STOP = new File(ROOT + ".stop");
    static final File STATUS = new File(ROOT + ".status");
    static final String MAIN = StandaloneWatchdog.class.getName();
    static final Pattern PROXIMITY = Pattern.compile("Virtual proximity state:\\s*(\\S+)");
    static final Pattern WAKEFULNESS = Pattern.compile("mWakefulness=(\\S+)");
    static final Pattern STAY_ON = Pattern.compile("mStayOn=(true|false)");
    // Power/display/stay-on/wake/proximity/broadcast each have a five-second budget.
    static final long COMMAND_BUDGET_MS = 30000;

    public static void main(String[] args) throws Exception {
        if (args.length == 1 && "status".equals(args[0])) {
            printStatus();
        } else if (args.length == 1 && "stop".equals(args[0])) {
            STOP.createNewFile();
            System.out.println("stop_requested=true");
        } else if (args.length == 4 && "start".equals(args[0])) {
            long holdMs = Long.parseLong(args[1]);
            long intervalMs = Long.parseLong(args[2]);
            boolean untilStopped = "until-stopped".equals(args[3]);
            long sessionMs = untilStopped ? 28800000L : Long.parseLong(args[3]);
            // All timing validation precedes the lease and stop-marker mutation.
            validateTiming(holdMs, intervalMs, sessionMs);
            ProximityHold hold = new ProximityHold(holdMs, intervalMs, COMMAND_BUDGET_MS);
            try (ProximityWatchdogLease lease = ProximityWatchdogLease.acquire(STOP)) {
                if (lease == null) throw new IllegalStateException("Watchdog already owns stop marker");
                run(hold, sessionMs, untilStopped);
            }
        } else {
            throw new IllegalArgumentException("Expected status, stop, or start <hold-ms> <poll-ms> <session-ms|until-stopped>");
        }
    }

    static void validateTiming(long holdMs, long intervalMs, long sessionMs) {
        if (holdMs < 60000 || holdMs > 28800000 || intervalMs < 1000 || intervalMs > 60000 ||
                sessionMs < 60000 || sessionMs > 28800000) {
            throw new IllegalArgumentException("Hold/session must be 60000..28800000 ms; poll 1000..60000 ms");
        }
        new ProximityHold(holdMs, intervalMs, COMMAND_BUDGET_MS);
    }

    // Android's clock includes suspend; do not substitute host wall time or Java nanoTime here.
    static long elapsedMs() throws Exception {
        return ((Long) Class.forName("android.os.SystemClock").getMethod("elapsedRealtime").invoke(null)).longValue();
    }

    private static void run(final ProximityHold hold, final long sessionMs, final boolean untilStopped) throws Exception {
        final long started = elapsedMs();
        final ProximityHold.Environment environment = new ProximityHold.Environment() {
            public long elapsedMs() {
                try { return StandaloneWatchdog.elapsedMs(); }
                catch (Exception error) { throw new IllegalStateException(error); }
            }
            public boolean stopped() {
                return ProximityHold.sessionEnded(elapsedMs(), started, sessionMs, untilStopped,
                    STOP.exists() || Thread.currentThread().isInterrupted());
            }
            public ProximityHold.Result broadcast(long holdMs) throws Exception {
                CommandCapture result = runBoundedCommand(new String[] {"am", "broadcast", "-a",
                    "com.oculus.vrpowermanager.prox_close", "--ei", "duration", Long.toString(holdMs)}, 16384, 5);
                return new ProximityHold.Result(result.success(), result.success() ? "" : result.summary());
            }
        };
        Properties status = new Properties();
        String[] self = readText(new File("/proc/self/stat")).split(" ", 2);
        String pid = self[0];
        status.setProperty("pid", pid);
        status.setProperty("process_start_ticks", processStartTicks(readText(new File("/proc/" + pid + "/stat"))));
        status.setProperty("process_cmdline_sha256", sha256(Files.readAllBytes(new File("/proc/" + pid + "/cmdline").toPath())));
        status.setProperty("boot_id", readText(new File("/proc/sys/kernel/random/boot_id")).trim());
        status.setProperty("startup_jar_sha256", currentJarHash());
        status.setProperty("interval_ms", Long.toString(hold.intervalMs));
        status.setProperty("hold_ms", Long.toString(hold.holdMs));
        status.setProperty("renew_after_ms", Long.toString(hold.renewAfterMs));
        status.setProperty("command_budget_ms", Long.toString(COMMAND_BUDGET_MS));
        status.setProperty("until_stopped", Boolean.toString(untilStopped));
        status.setProperty("session_ms", Long.toString(sessionMs));
        long attempts = 0;
        long failures = 0;
        try {
            while (!environment.stopped()) {
                String action = "observed";
                CommandCapture power = readPowerState();
                String wakefulness = power.success() ? match(WAKEFULNESS, power.output) : "";
                String stayOn = power.success() ? match(STAY_ON, power.output) : "";
                CommandCapture physicalDisplay = readDisplayState();
                String display = physicalDisplay.success() ? physicalDisplayState(physicalDisplay.output) : "";
                status.setProperty("wakefulness", wakefulness);
                status.setProperty("stay_on", stayOn);
                status.setProperty("display_state", display);
                status.setProperty("display_state_source", display.isEmpty() ? "unavailable" : "dumpsys-display-internal-controller-0");
                // Missing fields are unavailable observations, never invented drift.
                if ("false".equals(stayOn) && !environment.stopped()) {
                    CommandCapture result = runBoundedCommand(new String[] {"svc", "power", "stayon", "true"}, 16384, 5);
                    action = result.success() ? "reapplied_stay_awake" : "stay_awake_failed";
                }
                if (shouldWake(wakefulness, display) && !environment.stopped()) {
                    CommandCapture result = runBoundedCommand(new String[] {"input", "keyevent", "224"}, 16384, 5);
                    action = result.success() ? "reapplied_wake" : "wake_failed";
                }
                CommandCapture proximity = readProximityState();
                String virtualState = proximity.success() ? match(PROXIMITY, proximity.output) : "";
                status.setProperty("virtual_proximity", virtualState);
                ProximityHold.Result broadcast = hold.poll(!virtualState.isEmpty(), virtualState, environment);
                if (broadcast != null) {
                    attempts++;
                    if (!broadcast.success) failures++;
                    action = broadcast.success ? ("CLOSE".equalsIgnoreCase(virtualState)
                        ? "renewed_prox_close" : "reapplied_prox_close") : "broadcast_failed";
                    System.out.println("action=" + action + " success=" + broadcast.success +
                        " last_success_elapsed_ms=" + (hold.hasSuccess ? hold.lastSuccessElapsedMs : -1) +
                        " renew_after_ms=" + hold.renewAfterMs);
                } else if (virtualState.isEmpty()) {
                    action = "proximity_read_unavailable";
                }
                status.setProperty("last_action", action);
                status.setProperty("hold_attempts", Long.toString(attempts));
                status.setProperty("hold_failures", Long.toString(failures));
                status.setProperty("last_success_elapsed_ms", hold.hasSuccess ? Long.toString(hold.lastSuccessElapsedMs) : "unknown");
                writeStatus(status, true);
                if (environment.stopped()) break;
                long remaining = untilStopped ? hold.intervalMs : sessionMs - (elapsedMs() - started);
                if (remaining > 0) Thread.sleep(Math.min(hold.intervalMs, remaining));
            }
        } finally {
            writeStatus(status, false);
        }
    }

    static boolean shouldWake(String wakefulness, String display) {
        return (!wakefulness.isEmpty() && !"Awake".equalsIgnoreCase(wakefulness)) ||
            (!display.isEmpty() && !"ON".equalsIgnoreCase(display) && !"ON_SUSPEND".equalsIgnoreCase(display));
    }

    static String match(Pattern pattern, String text) {
        java.util.regex.Matcher matcher = pattern.matcher(text);
        return matcher.find() ? matcher.group(1).trim() : "";
    }

    static String physicalDisplayState(String text) {
        String[] controllers = text.replace("\r", "").split("(?m)^[ \\t]*Display Power Controller:[ \\t]*$", -1);
        Pattern flags = Pattern.compile("(?m)^[ \\t]*mIsDisplayInternal=([^\\n]*)$");
        Pattern ids = Pattern.compile("(?m)^[ \\t]*mDisplayId=([^\\n]*)$");
        Pattern states = Pattern.compile("(?m)^[ \\t]*mScreenState=([^\\n]*)$");
        int internalCount = 0;
        String result = "";
        for (int index = 1; index < controllers.length; index++) {
            String block = controllers[index];
            java.util.regex.Matcher flag = flags.matcher(block);
            int flagCount = 0;
            boolean internal = false;
            while (flag.find()) { flagCount++; internal |= "true".equals(flag.group(1).trim()); }
            if (!internal) continue;
            internalCount++;
            if (flagCount != 1) return "";
            java.util.regex.Matcher id = ids.matcher(block);
            boolean foundId = false;
            while (id.find()) {
                String value = id.group(1).trim();
                // Nested display subsections use the same ID, sometimes written ': 0'.
                if (value.startsWith(":")) value = value.substring(1).trim();
                if (!"0".equals(value)) return "";
                foundId = true;
            }
            java.util.regex.Matcher state = states.matcher(block);
            if (!foundId || !state.find()) return "";
            result = state.group(1).trim();
            if (state.find() || !result.matches("ON|ON_SUSPEND|OFF|DOZE|DOZE_SUSPEND|VR")) return "";
        }
        return internalCount == 1 ? result : "";
    }

    static CommandCapture readPowerState() { return read(new String[] {"dumpsys", "power"}); }
    static CommandCapture readDisplayState() { return read(new String[] {"dumpsys", "display"}); }
    static CommandCapture readProximityState() { return read(new String[] {"dumpsys", "vrpowermanager"}); }
    private static CommandCapture read(String[] command) {
        try { return runBoundedCommand(command, 262144, 5); }
        catch (Exception error) { return new CommandCapture("", false, -1, true); }
    }

    static final class CommandCapture {
        final String output;
        final boolean timedOut;
        final int exitCode;
        final boolean truncated;
        CommandCapture(String output, boolean timedOut, int exitCode, boolean truncated) {
            this.output = output; this.timedOut = timedOut; this.exitCode = exitCode; this.truncated = truncated;
        }
        boolean success() { return exitCode == 0 && !timedOut && !truncated; }
        String summary() { return "exit=" + exitCode + " timeout=" + timedOut + " truncated=" + truncated; }
    }

    // Adapted from pinned Helper.runBoundedCommand; retain timeout/cancellation behavior.
    static CommandCapture runBoundedCommand(String[] command, int maxBytes, int timeoutSeconds) throws Exception {
        if (Thread.currentThread().isInterrupted()) throw new InterruptedException("Command cancelled before dispatch");
        Process process = new ProcessBuilder(command).redirectErrorStream(true).start();
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[8192];
        boolean truncated = false;
        boolean timedOut = false;
        long startedNs = System.nanoTime();
        long timeoutNs = TimeUnit.SECONDS.toNanos(timeoutSeconds);
        try {
            InputStream input = process.getInputStream();
            while (true) {
                if (System.nanoTime() - startedNs >= timeoutNs) { timedOut = true; break; }
                int available = input.available();
                if (available <= 0) {
                    if (!process.isAlive()) break;
                    Thread.sleep(10);
                    continue;
                }
                int count = input.read(buffer, 0, Math.min(buffer.length, available));
                if (count < 0) break;
                int allowed = Math.min(count, maxBytes - output.size());
                if (allowed > 0) output.write(buffer, 0, allowed);
                if (allowed < count) { truncated = true; process.destroy(); break; }
            }
            long remainingNs = Math.max(0, timeoutNs - (System.nanoTime() - startedNs));
            if (timedOut || !process.waitFor(remainingNs, TimeUnit.NANOSECONDS)) timedOut = true;
            return new CommandCapture(new String(output.toByteArray(), StandardCharsets.UTF_8),
                timedOut, timedOut ? -1 : process.exitValue(), truncated);
        } catch (InterruptedException cancelled) {
            Thread.currentThread().interrupt();
            throw cancelled;
        } finally {
            boolean interrupted = Thread.interrupted();
            try {
                try { process.destroy(); } catch (Exception unknown) { /* Retain the child below. */ }
                try { if (process.isAlive()) process.destroyForcibly(); }
                catch (Exception unknown) { /* A failed kill never releases custody. */ }
                try { process.getOutputStream().close(); } catch (Exception ignored) { }
                if (process.isAlive()) System.out.println("command_retirement_pending=true");
                // Quiesce while retirement is unknown: no later command or lease release.
                while (process.isAlive()) {
                    try { process.waitFor(250, TimeUnit.MILLISECONDS); }
                    catch (InterruptedException cancelled) { interrupted = true; }
                }
                // Retain inherited output handles until EOF, discarding beyond capture.
                // A lingering writer keeps this owner quiescent instead of granting retry.
                try { while (process.getInputStream().read(buffer) >= 0) { } }
                catch (IOException terminalReadFailure) { }
                try { while (process.getErrorStream().read(buffer) >= 0) { } }
                catch (IOException terminalReadFailure) { }
                try { process.getInputStream().close(); } catch (Exception ignored) { }
                try { process.getErrorStream().close(); } catch (Exception ignored) { }
            } finally { if (interrupted) Thread.currentThread().interrupt(); }
        }
    }

    static String readText(File file) throws IOException {
        return new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8);
    }

    static String processStartTicks(String stat) {
        // comm may contain spaces/parentheses. Field 22 is index 19 after the final ')'.
        String[] fields = stat.substring(stat.lastIndexOf(')') + 2).trim().split("\\s+");
        if (fields.length < 20 || !fields[19].matches("[0-9]+")) throw new IllegalArgumentException("Invalid proc stat");
        return fields[19];
    }

    static String sha256(byte[] bytes) throws Exception {
        StringBuilder text = new StringBuilder();
        for (byte value : java.security.MessageDigest.getInstance("SHA-256").digest(bytes)) {
            text.append(String.format(java.util.Locale.ROOT, "%02x", value & 255));
        }
        return text.toString();
    }

    static String currentJarHash() throws Exception {
        String path = System.getenv("CLASSPATH");
        if (path == null || !path.matches(Pattern.quote(ROOT) + "-[0-9a-f]{64}\\.jar")) {
            throw new IllegalStateException("Expected QFM content-addressed standalone JAR");
        }
        String digest = sha256(Files.readAllBytes(new File(path).toPath()));
        if (!path.equals(ROOT + "-" + digest + ".jar")) throw new IllegalStateException("Startup JAR hash mismatch");
        return digest;
    }

    private static void writeStatus(Properties values, boolean active) throws Exception {
        values.setProperty("active", Boolean.toString(active));
        values.setProperty("last_poll_elapsed_ms", Long.toString(elapsedMs()));
        File temporary = new File(STATUS.getPath() + ".tmp");
        try (OutputStream stream = new FileOutputStream(temporary)) { values.store(stream, "QFM standalone watchdog v1"); }
        Files.move(temporary.toPath(), STATUS.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING);
    }

    private static void printStatus() throws Exception {
        Properties status = new Properties();
        boolean identity = false;
        boolean held = false;
        if (STATUS.isFile()) {
            try (InputStream stream = new FileInputStream(STATUS)) { status.load(stream); }
            String pid = status.getProperty("pid", "");
            if (pid.matches("[0-9]+")) {
                try {
                    identity = status.getProperty("boot_id", "").equals(readText(new File("/proc/sys/kernel/random/boot_id")).trim()) &&
                        status.getProperty("process_start_ticks", "").equals(processStartTicks(readText(new File("/proc/" + pid + "/stat")))) &&
                        status.getProperty("process_cmdline_sha256", "").equals(sha256(Files.readAllBytes(new File("/proc/" + pid + "/cmdline").toPath()))) &&
                        java.util.Arrays.asList(readText(new File("/proc/" + pid + "/cmdline")).split("\u0000")).contains(MAIN);
                } catch (Exception absent) { identity = false; }
            }
        }
        File lockFile = new File(STOP.getPath() + ".lock");
        if (lockFile.isFile()) {
            try (RandomAccessFile file = new RandomAccessFile(lockFile, "rw")) {
                try (FileLock lock = file.getChannel().tryLock()) { held = lock == null; }
                catch (OverlappingFileLockException owned) { held = true; }
            }
        }
        boolean active = identity && held && "true".equals(status.getProperty("active"));
        long age = -1;
        long interval = 5000;
        try {
            age = elapsedMs() - Long.parseLong(status.getProperty("last_poll_elapsed_ms", "-1"));
            interval = Long.parseLong(status.getProperty("interval_ms", "5000"));
        } catch (Exception unavailable) { age = -1; }
        System.out.println("identity_matches=" + identity);
        System.out.println("lease_held=" + held);
        System.out.println("owner_active=" + (identity && held));
        System.out.println("active=" + active);
        System.out.println("fresh=" + (active && age >= 0 && age <= COMMAND_BUDGET_MS + 3 * interval));
        System.out.println("stop_requested=" + STOP.exists());
        for (String key : new String[] {"pid", "process_start_ticks", "process_cmdline_sha256", "startup_jar_sha256", "boot_id", "interval_ms", "hold_ms", "renew_after_ms", "command_budget_ms", "display_state_source",
                "until_stopped", "session_ms", "last_poll_elapsed_ms", "last_success_elapsed_ms", "hold_attempts", "hold_failures",
                "last_action", "wakefulness", "stay_on", "display_state", "virtual_proximity"}) {
            System.out.println(key + "=" + status.getProperty(key, "unknown"));
        }
    }
}

package com.example.rustyxr.shell;

/** Local finite-hold accounting; CLOSE alone never authenticates remaining time. */
final class ProximityHold {
    interface Environment {
        long elapsedMs();
        boolean stopped();
        Result broadcast(long holdMs) throws Exception;
    }

    static final class Result {
        final boolean success;
        final String error;

        Result(boolean success, String error) {
            this.success = success;
            this.error = error;
        }
    }

    final long holdMs;
    final long intervalMs;
    final long renewAfterMs;
    boolean hasSuccess;
    long lastSuccessElapsedMs;
    private boolean attempted;
    private long lastAttemptCompletedMs;

    ProximityHold(long holdMs, long intervalMs, long commandBudgetMs) {
        if (holdMs < 1 || holdMs > Integer.MAX_VALUE || intervalMs < 1 ||
                intervalMs > 60000 || commandBudgetMs < 0 || commandBudgetMs > 60000) {
            throw new IllegalArgumentException("Invalid proximity hold timing");
        }
        long marginMs = Math.max(holdMs / 4, 2 * (intervalMs + commandBudgetMs));
        if (marginMs >= holdMs) {
            throw new IllegalArgumentException("Proximity hold must exceed twice the poll and command budget");
        }
        this.holdMs = holdMs;
        this.intervalMs = intervalMs;
        this.renewAfterMs = holdMs - marginMs;
    }

    Result poll(boolean available, String virtualState, Environment environment) throws Exception {
        if (environment.stopped() || !available) {
            return null;
        }
        long now = environment.elapsedMs();
        if (attempted && now - lastAttemptCompletedMs < intervalMs) {
            return null;
        }
        if ("CLOSE".equalsIgnoreCase(virtualState) && hasSuccess &&
                now - lastSuccessElapsedMs < renewAfterMs) {
            return null;
        }
        if (environment.stopped()) {
            return null;
        }
        long issuedAt = environment.elapsedMs();
        Result result;
        try {
            result = environment.broadcast(holdMs);
        } catch (InterruptedException cancelled) {
            throw cancelled;
        } catch (Exception failure) {
            result = new Result(false, failure.toString());
        }
        attempted = true;
        lastAttemptCompletedMs = environment.elapsedMs();
        if (result.success) {
            hasSuccess = true;
            lastSuccessElapsedMs = issuedAt;
        }
        return result;
    }

    static boolean sessionEnded(long nowMs, long startedMs, long durationMs,
            boolean untilStopped, boolean stopped) {
        return stopped || (!untilStopped && nowMs - startedMs >= durationMs);
    }
}

package com.example.rustyxr.shell;

import java.io.File;
import java.io.RandomAccessFile;
import java.nio.channels.FileLock;
import java.nio.channels.OverlappingFileLockException;

/** Own the shared stop marker for the entire watchdog lifetime. */
final class ProximityWatchdogLease implements AutoCloseable {
    private final RandomAccessFile file;
    private final FileLock lock;

    private ProximityWatchdogLease(RandomAccessFile file, FileLock lock) {
        this.file = file;
        this.lock = lock;
    }

    static ProximityWatchdogLease acquire(File stopFile) throws Exception {
        RandomAccessFile file = new RandomAccessFile(stopFile.getPath() + ".lock", "rw");
        FileLock lock = null;
        try {
            try {
                lock = file.getChannel().tryLock();
            } catch (OverlappingFileLockException duplicate) {
                return null;
            }
            if (lock == null) return null;
            if (stopFile.exists() && !stopFile.delete()) {
                throw new IllegalStateException("Could not clear stale proximity watchdog stop marker");
            }
            ProximityWatchdogLease lease = new ProximityWatchdogLease(file, lock);
            file = null;
            return lease;
        } finally {
            if (file != null) {
                if (lock != null) lock.release();
                file.close();
            }
        }
    }

    public void close() throws Exception {
        try { lock.release(); } finally { file.close(); }
    }
}

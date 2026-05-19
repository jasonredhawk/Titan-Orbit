#!/usr/bin/env bash
# Run on the VM when serial console is flooded by titanorbit-server restart loops.
# Safe to paste in browser SSH or serial console after login.
set -e
echo "Stopping and disabling titanorbit-server (stops the restart loop on boot)..."
sudo systemctl stop titanorbit-server 2>/dev/null || true
sudo systemctl disable titanorbit-server 2>/dev/null || true
sudo systemctl reset-failed titanorbit-server 2>/dev/null || true
# mask = nothing can start it until: sudo systemctl unmask titanorbit-server
sudo systemctl mask titanorbit-server 2>/dev/null || true
echo "Done. Service is stopped, disabled, and masked."
echo "Re-enable after fixing the build: sudo systemctl unmask titanorbit-server && sudo systemctl enable titanorbit-server"
systemctl is-enabled titanorbit-server 2>/dev/null || echo "(masked/disabled)"

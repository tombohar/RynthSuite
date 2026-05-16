using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthAi.Raycasting
{
    public class BlacklistManager
    {
        /// <summary>
        /// Tracks consecutive failures for specific WorldObjects.
        /// Incremented when an attack on a target produces no server feedback.
        /// </summary>
        private readonly Dictionary<int, int> _failureCounts = new Dictionary<int, int>();

        /// <summary>
        /// Tracks the expiration time for blacklisted targets.
        /// Targets are temporarily ignored if they're stuck in walls or ghosts.
        /// </summary>
        private readonly Dictionary<int, DateTime> _blacklistExpirations = new Dictionary<int, DateTime>();

        /// <summary>Optional diagnostic sink — invoked whenever a target is blacklisted.</summary>
        public Action<string>? Log;

        /// <summary>
        /// Number of consecutive attack failures before a target is blacklisted.
        /// Default: 3 attempts (adjustable for different difficulty settings).
        /// </summary>
        public int AttemptThreshold { get; set; } = 1;

        /// <summary>
        /// Duration in seconds that a target remains blacklisted after threshold is breached.
        /// Default: 300 seconds — long enough that non-attackable objects stay gone for the session.
        /// </summary>
        public int TimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Increments the failure count for a target.
        /// Call this if an attack is launched but yields no server feedback (damage, XP, etc.).
        /// If the failure count reaches the threshold, the target is automatically blacklisted.
        /// </summary>
        public void ReportFailure(int targetId)
        {
            if (!_failureCounts.ContainsKey(targetId))
                _failureCounts[targetId] = 0;

            _failureCounts[targetId]++;

            // Log the failure for debugging
            System.Diagnostics.Debug.WriteLine($"Target {targetId} failure count: {_failureCounts[targetId]}/{AttemptThreshold}");

            // If the threshold is breached, blacklist the target
            if (_failureCounts[targetId] >= AttemptThreshold)
            {
                BlacklistTarget(targetId);
            }
        }

        /// <summary>
        /// Immediately blacklists a target for the configured timeout duration.
        /// </summary>
        private void BlacklistTarget(int targetId)
        {
            DateTime expirationTime = DateTime.Now.AddSeconds(TimeoutSeconds);
            _blacklistExpirations[targetId] = expirationTime;

            int fc = _failureCounts.TryGetValue(targetId, out var c) ? c : 0;
            Log?.Invoke($"0x{(uint)targetId:X8} blacklisted {TimeoutSeconds}s (failures={fc})");
            System.Diagnostics.Debug.WriteLine($"Target {targetId} blacklisted until {expirationTime:HH:mm:ss}");
        }

        /// <summary>
        /// Checks if a target should be ignored by the targeting system.
        /// Returns true if the target is currently blacklisted (timeout not expired).
        /// Returns false if the target is not blacklisted or if its timeout has expired.
        /// </summary>
        public bool IsBlacklisted(int targetId)
        {
            if (!_blacklistExpirations.TryGetValue(targetId, out var expiration))
                return false; // Target not in blacklist

            // Check if blacklist timeout has expired
            if (DateTime.Now >= expiration)
            {
                // Timeout expired - remove from blacklist and allow re-evaluation
                _blacklistExpirations.Remove(targetId);
                _failureCounts.Remove(targetId);
                System.Diagnostics.Debug.WriteLine($"Target {targetId} blacklist expired, re-evaluating");
                return false;
            }

            // Target still blacklisted
            return true;
        }

        /// <summary>
        /// Clears the failure count and blacklist status for a target.
        /// Call this when a target successfully takes damage or shows other server feedback.
        /// This allows the target to be re-engaged if it becomes blocked again later.
        /// </summary>
        public void ClearFailure(int targetId)
        {
            _failureCounts.Remove(targetId);
            _blacklistExpirations.Remove(targetId);
            System.Diagnostics.Debug.WriteLine($"Target {targetId} failure cleared");
        }

        /// <summary>
        /// Completely clears all blacklist and failure tracking data.
        /// Useful when transitioning between combat zones or resetting the bot state.
        /// </summary>
        public void ClearAll()
        {
            _failureCounts.Clear();
            _blacklistExpirations.Clear();
            System.Diagnostics.Debug.WriteLine("All blacklist data cleared");
        }

        /// <summary>
        /// Returns the number of currently blacklisted targets.
        /// Useful for debugging and monitoring bot behavior.
        /// </summary>
        public int GetBlacklistCount()
        {
            int count = 0;
            var expiredKeys = new List<int>();

            foreach (var kvp in _blacklistExpirations)
            {
                if (DateTime.Now < kvp.Value)
                {
                    count++;
                }
                else
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            // Clean up expired entries while we're here
            foreach (var key in expiredKeys)
            {
                _blacklistExpirations.Remove(key);
                _failureCounts.Remove(key);
            }

            return count;
        }
    }
}

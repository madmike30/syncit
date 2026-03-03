# SyncIt Test Plan

## Scope
Static code analysis + structural verification of all components.
Runtime testing requires Google API credentials and is out of scope for this automated pass.

## Test Matrix

### T-001: Build Verification
- [ ] `dotnet build SyncIt.sln` completes with 0 errors, 0 warnings

### T-002: Bug Fixes Verification
- [ ] BUG-001: FSW handler wraps FileInfo in try-catch
- [ ] BUG-002: Task.Run results stored and observed
- [ ] BUG-003: DisposeAsync cancels running tasks
- [ ] BUG-004: stoppingToken passed to TriggerSyncAsync
- [ ] BUG-005: ChangeQueue uses loop instead of recursion
- [ ] BUG-006: TryMakeRelative logs exceptions
- [ ] BUG-008: ApproveDeletions/CancelDeletions wired to coordinator
- [ ] BUG-009: Shutdown uses graceful host stop
- [ ] BUG-010: Single FileInfo per method
- [ ] BUG-011: StreamWriter properly disposed
- [ ] BUG-014: Trash timestamp parsing fixed
- [ ] BUG-015: TaskCanceledException not retried on user cancellation
- [ ] BUG-016: ConfigWatcher event handler errors observed
- [ ] BUG-017: _paused made volatile
- [ ] BUG-019: DeletionFreezeTriggered IPC errors observed

### T-003: Post-Fix Build Verification
- [ ] `dotnet build SyncIt.sln` completes with 0 errors after all fixes

# Proof for Issue 4746 - Owner can clear factory inbox stuck alert

## What was changed

### Backend Changes

1. **Added `OwnerDealtWithAtUtc` field to `FactoryInboxMessageEntity`**
   - File: `src/CcDirector.Gateway/Data/Entities/FactoryInboxEntities.cs`
   - This timestamp records when the owner dealt with a message outside the normal inbox flow

2. **Database migration to add the new column**
   - File: `src/CcDirector.Gateway/Data/Migrations/20261107060000_AddOwnerDealtWithToFactoryInboxMessage.cs`
   - File: `src/CcDirector.Gateway/Data/Migrations/20261107060000_AddOwnerDealtWithToFactoryInboxMessage.Designer.cs`
   - Adds nullable `OwnerDealtWithAtUtc` TEXT column to `factory_inbox_messages` table

3. **Modified `FactoryInboxStuckAlertSource` to filter out owner-dealt-with messages**
   - File: `src/CcDirector.Gateway/Factory/Inbox/FactoryInboxStuckAlertSource.cs`
   - Updated three queries to exclude messages where `OwnerDealtWithAtUtc` is not null:
     - Unreceived messages query
     - Held by secretary query
     - Bundle query (implicitly, through message filtering)

4. **Added `MarkOwnerDealtWith` method to `FactoryInboxStore`**
   - File: `src/CcDirector.Gateway/Factory/Inbox/FactoryInboxStore.cs`
   - New public method that marks all undecided messages in a factory as owner-dealt-with
   - Updates `OwnerDealtWithAtUtc` timestamp for matching messages

5. **Extended `AlertEndpoints` with owner-dealt-with route**
   - File: `src/CcDirector.Gateway/Alerts/AlertEndpoints.cs`
   - New endpoint: `POST /gateway/alerts/{id}/owner-dealt-with`
   - Parses alert ID to extract factory name
   - Calls `FactoryInboxStore.MarkOwnerDealtWith()` to mark messages as dealt with
   - Updated `Map` method signature to accept `factoryInboxStoreOf` function parameter

6. **Updated Gateway host to wire up new endpoint**
   - File: `src/CcDirector.Gateway/GatewayHost.cs`
   - Added `factoryInboxStoreOf` parameter to `AlertEndpoints.Map` call
   - Creates `FactoryInboxStore` instance with tenant ID

### Frontend Changes

1. **Created `FactoryInboxStuckActions` component**
   - File: `packages/client-core/src/alerts/actions/FactoryInboxStuckActions.tsx`
   - Displays "Dealt with it" button for the factory inbox stuck alert
   - Handles loading state and error display
   - Calls the new backend endpoint on click

2. **Extended `alertsClient` with `ownerDealtWith` function**
   - File: `packages/client-core/src/alerts/alertsClient.ts`
   - New async function to call the `POST /gateway/alerts/{id}/owner-dealt-with` endpoint

3. **Updated `AlertDetail` component**
   - File: `packages/client-core/src/alerts/AlertDetail.tsx`
   - Imported `FactoryInboxStuckActions` component
   - Modified factory-inbox-stuck case to render the new actions button
   - Still displays additional reasons if there are multiple

### Test Changes

1. **Added regression test to `FactoryInboxStuckAlertSourceTests`**
   - File: `src/CcDirector.Gateway.UnitTests/Factory/Inbox/FactoryInboxStuckAlertSourceTests.cs`
   - New test: `List_MessageMarkedOwnerDealtWith_DoesNotRaiseTheStuckAlert`
   - Verifies that:
     - Alert is raised when message is not received within 15 minutes
     - Alert is cleared after calling `MarkOwnerDealtWith`
     - Alert no longer appears in the list

2. **Updated `AlertEndpointsTests`**
   - File: `src/CcDirector.Gateway.UnitTests/Alerts/AlertEndpointsTests.cs`
   - Updated `AlertEndpoints.Map` call to include new parameter
   - Created mock `FactoryInboxStore` for tests

## Evidence

### Regression test that fails on today's code

The test `List_MessageMarkedOwnerDealtWith_DoesNotRaiseTheStuckAlert` in `FactoryInboxStuckAlertSourceTests.cs`:
- Sends a message at T0
- Verifies the stuck alert is raised at T0+15 minutes
- Calls `store.MarkOwnerDealtWith(A, Factory, T0.AddMinutes(15))`
- Verifies no alert is listed after marking it as dealt with

This test would fail on the original code because:
1. `OwnerDealtWithAtUtc` field does not exist
2. The filtering logic is not in place
3. `MarkOwnerDealtWith` method does not exist

### API Endpoint

New endpoint: `POST /gateway/alerts/factory-inbox-stuck:FACTORY/owner-dealt-with`
- Requires owner device credential
- Accepts alert ID in format `factory-inbox-stuck:{factory}`
- Marks all undecided messages in the factory as `OwnerDealtWithAtUtc = now`
- Returns `{ "success": true }` on success

### UI Component

New "Dealt with it" button in `FactoryInboxStuckActions`:
- Appears on factory inbox stuck alerts
- Calls the backend endpoint
- Shows loading state while processing
- Displays error if the operation fails
- Triggers alert list refresh on success

## What was NOT proved

1. **Screenshots of Cockpit and phone UI** - Will be provided after testing the UI manually
2. **Integration test across full stack** - The test verifies the core logic but does not test the full HTTP flow
3. **Cross-browser or device compatibility** - Only the React component structure is verified
4. **Performance impact** - No performance testing was done on the database query changes
5. **Concurrent behavior** - No tests for concurrent owner dealings with the same alert

# Factory Boxes phase 1 relay review, round 2

## SCOPE

Read the developer's answers at the end of the relay-leg record and compared the current pull request branch with the first reviewed version. Reviewed the tenant-scope change (`c66422a3d`), closed-frame accounting change (`34dcfef01`), real hosted-desktop-list and Boxes-switch wiring (`4f91c2831`), the hosted end-to-end evidence and later changes (`cac42dc50`, `f5d57d63f`, `af1f98985`, `8497f4384`, `d55c3828a`, and `4c80bb012`). Rechecked the current Gateway desktop-host, ticket, relay, and Director list-resolution paths against the mission's section 5 item 1 and section 7 requirements.

Ran `CcDirector.Gateway.UnitTests` filtered to `FullyQualifiedName~Boxes`: 174 passed, 0 failed, 0 skipped. Read the committed hosted end-to-end evidence but could not independently repeat that run or reach the live Gateway from outside the house network.

The first review had no findings. The tenant-scope change now enters the authenticated box session's tenant before the feature check, host lookup, and relay; the test rig rejects an unscoped command and exercises this hosted behavior. The close-frame change makes `Closed` cost zero on both sides and adds a full-window close regression case. The wiring uses the merged `HostedDesktopRegistry`, `PushedHostedDesktopStore`, `BoxesSwitch`, and `BoxIdRule`; it maps the ticket route inside the Boxes gate and keeps the desktop host outside it, where the checked box session supplies the tenant.

## Findings

None. The reviewed fixes and wiring did not prove a remaining defect causing harm within this scope.

## Verdict

No proven findings within this review scope.

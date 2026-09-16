// The backdrop-dismiss hook lives in client-core now, so the shared colour legend closes by the same rule as every
// Cockpit overlay. The Cockpit keeps importing it from here; there is still exactly one implementation.
export { useDismissOnBackdrop, type BackdropDismissHandlers } from "@devthrottle/client-core/ui/useDismissOnBackdrop";

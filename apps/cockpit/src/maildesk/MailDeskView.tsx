import { MailDeskPage, type MailDeskFrameProps } from "@devthrottle/client-core/maildesk/MailDeskPage";
import { NotFound } from "../panes/NotFound";

// The Cockpit's Mail Desk page (Mail Desk mission, phase 7). The page is client-core's, one implementation for both
// shells (rule 8); this file is only the Cockpit's frame - the pane and its title - the Cockpit's own "Page not
// found", and its error banner's class (components/ErrorBanner.tsx). With the Mail Desk switch off the Gateway answers 404 and this address is the ordinary
// missing page, exactly as an unknown address looks.
function CockpitFrame({ title, children }: MailDeskFrameProps) {
  return (
    <section className="pane">
      {title !== null && <h1 className="pane-title">{title}</h1>}
      {children}
    </section>
  );
}

export function MailDeskView() {
  return (
    <MailDeskPage
      Frame={CockpitFrame}
      notFound={<NotFound />}
      errorClassName="ui-error-banner"
    />
  );
}

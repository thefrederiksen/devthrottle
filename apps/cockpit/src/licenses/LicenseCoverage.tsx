import type { LicenseCoverage } from "@devthrottle/client-core/licenses/licensesClient";
import { TonePill, toneClass } from "./licenseParts";

// THE COVERAGE GRID (mission "Licenses - Always Ready, Loud When Not", step 1): every machine that reports against every
// paid account, so the owner sees at a glance whether every machine could switch now. The Gateway folds every word and
// tone (LicenseCoverageFold); this draws them as given and decides nothing - a gap is yellow because the Gateway says
// "warning", never because a cell is empty.
export function LicenseCoverageGrid({ coverage }: { coverage: LicenseCoverage }) {
  return (
    <section aria-label="Coverage" data-testid="lic-coverage">
      <p className={toneClass(coverage.tone)} data-testid="lic-coverage-summary" data-tone={coverage.tone}>
        {coverage.text}
      </p>
      {coverage.rows.length > 0 && coverage.accounts.length > 0 && (
        <div className="lic-table-wrap">
          <table className="lic-table lic-coverage" data-testid="lic-coverage-grid">
            <thead>
              <tr>
                <th>Machine</th>
                {coverage.accounts.map((account) => (
                  <th key={account.accountEmail} data-testid="lic-coverage-account">
                    <div className="lic-coverage-account">{account.accountEmail}</div>
                    <div className="lic-dim">{account.planText}</div>
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {coverage.rows.map((row) => (
                <tr key={row.machine.machineId} data-testid="lic-coverage-row">
                  <td>
                    <div className="lic-strong lic-nowrap">{row.machine.label}</div>
                    <div className={toneClass(row.tone)} data-testid="lic-coverage-row-text" data-tone={row.tone}>
                      {row.text}
                    </div>
                  </td>
                  {row.cells.map((cell) => (
                    <td key={cell.accountEmail} className="lic-nowrap" data-testid="lic-coverage-cell" data-state={cell.state}>
                      <TonePill text={cell.text} tone={cell.tone} />
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

import { useAuth } from '../../../auth/AuthContext.jsx';
import { ProfileMenu, useSignOut } from '../../../components/TopNav.jsx';
import { formatDateTime } from '../../../utils/format.js';
import { limitName, limitText } from './healthModel.js';

/**
 * Health Dashboard card - the page's header: title and aircraft count, and the signed-in user's avatar (profile menu
 * with name, email, role and Sign out) at its top-right, above the overview (KPI cards, limits, recency).
 */
export function OverviewCard({ aircraftCount, thresholds = [], latestReadingAtUtc, children }) {
  const { user } = useAuth();
  const signOut = useSignOut();
  return (
    <section className="shrink-0 rounded-xl bg-gradient-to-r from-brand-deep via-brand to-brand-deep p-3 text-white" aria-label="Overview">
      <div className="flex items-center justify-between gap-3">
        <div className="flex min-w-0 items-baseline gap-3">
          <h1 className="m-0 text-[18px] font-semibold leading-tight text-white">Health Dashboard</h1>
          <span className="truncate text-[13px] text-white/80">{aircraftCount} aircraft</span>
        </div>
        {user ? <ProfileMenu user={user} onSignOut={signOut} /> : null}
      </div>
      <div className="pt-3" data-testid="overview-details">
        {children}
        <dl className="m-0 mt-2 grid gap-x-6 gap-y-1 text-[12px] sm:grid-cols-[auto_1fr]">
          <dt className="text-white/75">Limits in force</dt>
          <dd className="m-0">
            {thresholds.length
              ? thresholds.map((t) => (
                  <span key={t.code} className="mr-4 inline-block">
                    {limitName(t)} {limitText(t)}
                    {t.isValidated ? '' : ' (awaiting approval)'}
                  </span>
                ))
              : 'No measured limits configured'}
          </dd>
          <dt className="text-white/75">Readings up to</dt>
          <dd className="m-0">{latestReadingAtUtc ? formatDateTime(latestReadingAtUtc) : 'No readings yet'}</dd>
        </dl>
      </div>
    </section>
  );
}

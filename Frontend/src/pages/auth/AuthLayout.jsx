import { useEffect } from 'react';
import { BrandMark } from '../../components/Sidebar.jsx';
import { DemoBanner } from '../../components/Misc.jsx';

function SkyArt() {
  return (
    <svg className="auth-layout__art" viewBox="0 0 1200 800" preserveAspectRatio="xMidYMid slice" aria-hidden="true" focusable="false">
      <defs>
        <linearGradient id="trail" x1="0" x2="1">
          <stop offset="0" stopColor="#818cf8" stopOpacity="0" />
          <stop offset="1" stopColor="#c7d2fe" stopOpacity="0.9" />
        </linearGradient>
      </defs>
      <g stroke="#474a87" strokeWidth="1" fill="none" opacity="0.6">
        <circle cx="980" cy="160" r="90" />
        <circle cx="980" cy="160" r="170" />
        <circle cx="980" cy="160" r="260" />
        <path d="M980 160 L1180 40" />
        <path d="M0 640 Q300 560 600 620 T1200 580" />
        <path d="M0 700 Q300 640 600 690 T1200 660" />
      </g>
      <path d="M80 520 C 380 380, 640 300, 940 190" stroke="url(#trail)" strokeWidth="3" fill="none" strokeDasharray="2 10" strokeLinecap="round" />
      <g transform="translate(940 190) rotate(-24)" fill="#e0e7ff">
        <path d="M0 0 l-34 -6 -6 -18 -8 0 4 20 -30 -4 -10 -12 -6 0 4 16 -4 4 4 4 -4 16 6 0 10 -12 30 -4 -4 20 8 0 6 -18 34 -6z" />
      </g>
    </svg>
  );
}

export function AuthLayout({ title, children, documentTitle }) {
  useEffect(() => {
    document.title = `${documentTitle || title} · Aircraft Failure Prediction`;
  }, [title, documentTitle]);
  return (
    <>
      <DemoBanner />
      <div className="auth-layout">
        <SkyArt />
        <main className="auth-card" id="main-content">
          <div className="auth-card__brand">
            <span className="auth-card__brand-mark">
              <BrandMark size={22} />
            </span>
            <span className="auth-card__brand-name">
              Aircraft Failure Prediction
              <small>Fleet health &amp; maintenance platform</small>
            </span>
          </div>
          {children}
        </main>
      </div>
    </>
  );
}

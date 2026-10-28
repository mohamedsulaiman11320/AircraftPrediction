import { forwardRef } from 'react';
import { useRouter } from './Router.jsx';

function isModifiedEvent(e) {
  return !!(e.metaKey || e.altKey || e.ctrlKey || e.shiftKey);
}

/** Anchor that performs client-side navigation for plain same-origin left clicks. */
export const Link = forwardRef(function Link({ to, replace = false, onClick, target, children, ...rest }, ref) {
  const { navigate } = useRouter();
  const handleClick = (e) => {
    if (onClick) onClick(e);
    if (e.defaultPrevented) return;
    if (e.button !== 0 || isModifiedEvent(e) || (target && target !== '_self')) return;
    if (/^[a-z]+:\/\//i.test(to)) return;
    e.preventDefault();
    navigate(to, { replace });
  };
  return (
    <a ref={ref} href={to} onClick={handleClick} target={target} {...rest}>
      {children}
    </a>
  );
});

/**
 * Link that sets aria-current="page" when its path is active. `match` adds sibling path prefixes
 * that also count as active (e.g. a combined tab whose second screen lives elsewhere).
 */
export function NavLink({ to, exact = false, match, ...rest }) {
  const { pathname } = useRouter();
  const isActive = (path) => {
    const base = path.split('?')[0];
    return exact ? pathname === base : pathname === base || pathname.startsWith(base + '/');
  };
  const active = isActive(to) || (match || []).some(isActive);
  return <Link to={to} aria-current={active ? 'page' : undefined} {...rest} />;
}

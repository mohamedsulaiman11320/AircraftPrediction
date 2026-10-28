import { useAuth } from '../../auth/AuthContext.jsx';
import { ACCESS } from '../../router/access.js';
import { Link } from '../../router/Link.jsx';

/** Registration number, linked to the aircraft detail page only for roles that may open it. */
export function AircraftLink({ id, children, className }) {
  const { can } = useAuth();
  if (!id || !can(ACCESS.fleet)) return <span className={className}>{children}</span>;
  return (
    <Link to={`/fleet/${id}`} className={className}>
      {children}
    </Link>
  );
}

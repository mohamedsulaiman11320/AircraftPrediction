import { PageHeader } from '../components/PageHeader.jsx';
import { EmptyState } from '../components/States.jsx';
import { Link } from '../router/Link.jsx';

export function NotFoundPage() {
  return (
    <>
      <PageHeader title="Page not found" />
      <div className="card">
        <EmptyState
          icon="search"
          title="We could not find that page"
          message="The link may be out of date or the item may have been removed."
          action={
            <Link to="/" className="btn btn--primary">
              Go to my dashboard
            </Link>
          }
        />
      </div>
    </>
  );
}

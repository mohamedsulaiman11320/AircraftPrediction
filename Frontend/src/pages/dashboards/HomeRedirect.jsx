import { useAuth } from '../../auth/AuthContext.jsx';
import { defaultPathFor } from '../../router/access.js';
import { Redirect } from '../../router/Router.jsx';

export function HomeRedirect() {
  const { user } = useAuth();
  return <Redirect to={defaultPathFor(user)} />;
}

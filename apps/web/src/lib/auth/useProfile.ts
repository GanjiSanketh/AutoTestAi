import { useQuery } from '@tanstack/react-query';
import { useAuth } from '../../features/auth/AuthContext';
import { authEndpoints, type CurrentUserProfile } from '../api/endpoints/auth';

/** Shared current-profile query (single cache entry used by shell + pages). */
export function useProfile() {
  const { isAuthenticated } = useAuth();
  return useQuery<CurrentUserProfile>({
    queryKey: ['auth', 'me'],
    queryFn: authEndpoints.me,
    enabled: isAuthenticated,
    retry: false,
    staleTime: 60_000,
  });
}

import { Navigate, Outlet } from 'react-router';
import { useAuth } from '../context/AuthContext';
import type { UserRole } from '../context/AuthContext';

interface ProtectedRouteProps {
    allowedRoles?: UserRole[] | 'any';
}

export function ProtectedRoute({ allowedRoles }: ProtectedRouteProps) {
    const { user, isLoading } = useAuth();

    if (isLoading) {
        return <div className="min-h-screen flex items-center justify-center text-sm text-slate-500">Loading...</div>;
    }

    if (!user) {
        return <Navigate to="/login" replace />;
    }

    if (allowedRoles && allowedRoles !== 'any' && !allowedRoles.includes(user.role)) {
        return <Navigate to="/home" replace />;
    }

    return <Outlet />;
}

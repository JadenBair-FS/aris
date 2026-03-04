import { Navigate, Outlet } from 'react-router';
import { useAuth } from '../context/AuthContext';
import type { UserRole } from '../context/AuthContext';

interface ProtectedRouteProps {
    allowedRoles?: UserRole[] | 'any';
}

function AuthLoadingScreen() {
    return (
        <div className="min-h-screen flex flex-col items-center justify-center gap-3 bg-white">
            <span className="font-semibold text-slate-900 text-xl tracking-tight">ARIS</span>
            <svg className="animate-spin h-5 w-5 text-slate-400" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
            </svg>
            <p className="text-sm text-slate-400">Signing you in...</p>
        </div>
    );
}

export function ProtectedRoute({ allowedRoles }: ProtectedRouteProps) {
    const { user, isLoading } = useAuth();

    if (isLoading) return <AuthLoadingScreen />;

    if (!user) {
        return <Navigate to="/login" replace />;
    }

    if (allowedRoles && allowedRoles !== 'any' && !allowedRoles.includes(user.role)) {
        return <Navigate to="/home" replace />;
    }

    return <Outlet />;
}

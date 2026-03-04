import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import { useAuth } from './context/AuthContext';
import { DebugProvider } from './context/DebugContext';
import { AuthProvider } from './context/AuthContext';
import { ProtectedRoute } from './components/ProtectedRoute';
import { DashboardLayout } from './components/DashboardLayout';
import Landing from './pages/Landing';
import Login from './pages/auth/Login';
import Signup from './pages/auth/Signup';
import SeekerHome from './pages/seeker/Home';
import SeekerUpload from './pages/seeker/Upload';
import SeekerProfile from './pages/seeker/Profile';
import MatchDetail from './pages/seeker/MatchDetail';
import RecruiterHome from './pages/recruiter/Home';
import RecruiterPostJob from './pages/recruiter/PostJob';
import RecruiterJobList from './pages/recruiter/JobList';
import RecruiterJobDetail from './pages/recruiter/JobDetail';
import CandidateDetail from './pages/recruiter/CandidateDetail';

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

function SmartRoot() {
    const { user, isLoading } = useAuth();
    if (isLoading) return <AuthLoadingScreen />;
    if (!user) return <Landing />;
    return <Navigate to="/home" replace />;
}

function RoleHome() {
    const { user } = useAuth();
    if (user?.role === 'recruiter') return <RecruiterHome />;
    return <SeekerHome />;
}

function App() {
    return (
        <DebugProvider>
            <AuthProvider>
                <BrowserRouter>
                    <Routes>
                        {/* Public / smart root */}
                        <Route path="/" element={<SmartRoot />} />
                        <Route path="/login" element={<Login />} />
                        <Route path="/signup" element={<Signup />} />

                        {/* Shared authenticated home (role-aware) */}
                        <Route element={<ProtectedRoute allowedRoles="any" />}>
                            <Route element={<DashboardLayout />}>
                                <Route path="/home" element={<RoleHome />} />
                            </Route>
                        </Route>

                        {/* Seeker routes */}
                        <Route element={<ProtectedRoute allowedRoles={['seeker']} />}>
                            <Route element={<DashboardLayout />}>
                                <Route path="/profile/upload" element={<SeekerUpload />} />
                                <Route path="/profile" element={<SeekerProfile />} />
                                <Route path="/match/:profileId/:jobId" element={<MatchDetail />} />
                            </Route>
                        </Route>

                        {/* Recruiter routes */}
                        <Route element={<ProtectedRoute allowedRoles={['recruiter']} />}>
                            <Route element={<DashboardLayout />}>
                                <Route path="/profile/jobs/upload" element={<RecruiterPostJob />} />
                                <Route path="/profile/jobs" element={<RecruiterJobList />} />
                                <Route path="/profile/jobs/:jobId" element={<RecruiterJobDetail />} />
                                <Route path="/candidate/:profileId/:jobId" element={<CandidateDetail />} />
                            </Route>
                        </Route>

                        {/* Fallback */}
                        <Route path="*" element={<Navigate to="/" replace />} />
                    </Routes>
                </BrowserRouter>
            </AuthProvider>
        </DebugProvider>
    );
}

export default App;

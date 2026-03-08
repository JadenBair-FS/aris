import { useState, useEffect } from 'react';
import { Link, useNavigate } from 'react-router';
import { useSignUp, useAuth, useClerk } from '@clerk/clerk-react';
import { useAuth as useArisAuth } from '@/context/AuthContext';
import type { UserRole } from '@/context/AuthContext';
import { Input } from '@/components/ui/input';
import { Button } from '@/components/ui/button';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Loader2 } from 'lucide-react';

export default function Signup() {
    const [name, setName] = useState('');
    const [email, setEmail] = useState('');
    const [password, setPassword] = useState('');
    const [role, setRole] = useState<UserRole>('seeker');
    const [errorText, setErrorText] = useState('');
    const [isLoading, setIsLoading] = useState(false);

    const navigate = useNavigate();
    const { signUp, setActive, isLoaded } = useSignUp();
    const { getToken } = useAuth();
    const { user: clerkUser } = useClerk();
    const { user: arisUser } = useArisAuth();
    const [pendingRole, setPendingRole] = useState<UserRole | null>(null);

    useEffect(() => {
        if (pendingRole && arisUser?.role === pendingRole) {
            navigate(pendingRole === 'seeker' ? '/profile/upload' : '/profile/jobs/upload');
        }
    }, [pendingRole, arisUser?.role, navigate]);

    const handleSignup = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!isLoaded) return;
        setErrorText('');
        setIsLoading(true);

        try {
            const result = await signUp.create({
                firstName: name.split(' ')[0],
                lastName: name.split(' ').slice(1).join(' '),
                emailAddress: email,
                password,
            });

            if (result.status !== 'complete' || !result.createdSessionId) {
                setErrorText(`Sign-up incomplete (status: ${result.status}). Check your Clerk dashboard — email verification or bot protection may be blocking this.`);
                return;
            }

            await setActive({ session: result.createdSessionId });

            const token = await getToken();
            const res = await fetch('/api/auth/set-role', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'Authorization': `Bearer ${token}`,
                },
                body: JSON.stringify({ role }),
            });

            if (!res.ok) {
                const err = await res.json().catch(() => ({}));
                setErrorText((err as any).message ?? 'Failed to configure account. Please try again.');
                return;
            }

            // Force Clerk to re-fetch the user so publicMetadata.role is up to date.
            // Navigation is deferred via useEffect until AuthContext reflects the new role,
            // preventing a flash of the wrong role's dashboard.
            await clerkUser?.reload();
            setPendingRole(role);
        } catch (err: any) {
            setErrorText(err.errors?.[0]?.longMessage || err.message || 'Failed to sign up.');
        } finally {
            setIsLoading(false);
        }
    };

    return (
        <div className="min-h-screen flex">
            {/* Left brand panel */}
            <div className="hidden lg:flex flex-col justify-center px-12 w-[420px] shrink-0 bg-slate-900 text-white">
                <h1 className="text-3xl font-semibold tracking-tight mb-3">ARIS</h1>
                <p className="text-slate-400 text-sm leading-relaxed">
                    Automated Recruitment Intelligence System. AI-powered skill gap matching that connects the right people to the right roles.
                </p>
            </div>

            {/* Right form panel */}
            <div className="flex-1 flex items-center justify-center bg-white p-8">
                <div className="w-full max-w-sm">
                    <div className="mb-8">
                        <span className="lg:hidden font-semibold text-slate-900 text-xl">ARIS</span>
                        <h2 className="text-2xl font-semibold text-slate-900 mt-2">Create an account</h2>
                        <p className="text-slate-500 text-sm mt-1">Join the skill gap matching platform</p>
                    </div>

                    <form onSubmit={handleSignup} className="space-y-4">
                        {errorText && (
                            <Alert variant="destructive">
                                <AlertDescription>{errorText}</AlertDescription>
                            </Alert>
                        )}

                        {/* Role toggle */}
                        <div>
                            <p className="text-sm font-medium text-slate-700 mb-2">I am a:</p>
                            <div className="flex gap-2">
                                <button
                                    type="button"
                                    onClick={() => setRole('seeker')}
                                    disabled={isLoading}
                                    className={`flex-1 py-2 rounded-lg text-sm font-medium border transition-colors ${role === 'seeker'
                                            ? 'bg-slate-900 text-white border-slate-900'
                                            : 'bg-white text-slate-600 border-slate-200 hover:border-slate-400'
                                        }`}
                                >
                                    Job Seeker
                                </button>
                                <button
                                    type="button"
                                    onClick={() => setRole('recruiter')}
                                    disabled={isLoading}
                                    className={`flex-1 py-2 rounded-lg text-sm font-medium border transition-colors ${role === 'recruiter'
                                            ? 'bg-slate-900 text-white border-slate-900'
                                            : 'bg-white text-slate-600 border-slate-200 hover:border-slate-400'
                                        }`}
                                >
                                    Recruiter
                                </button>
                            </div>
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700">Full Name</label>
                            <Input
                                placeholder="Jane Doe"
                                value={name}
                                onChange={e => setName(e.target.value)}
                                required
                                disabled={isLoading}
                            />
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700">Email address</label>
                            <Input
                                type="email"
                                placeholder="name@example.com"
                                value={email}
                                onChange={e => setEmail(e.target.value)}
                                required
                                disabled={isLoading}
                            />
                        </div>

                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700">Password</label>
                            <Input
                                type="password"
                                placeholder="••••••••"
                                value={password}
                                onChange={e => setPassword(e.target.value)}
                                required
                                minLength={8}
                                disabled={isLoading}
                            />
                        </div>

                        <div id="clerk-captcha"></div>

                        <Button
                            type="submit"
                            className="w-full bg-slate-900 hover:bg-slate-800 text-white mt-2"
                            disabled={isLoading}
                        >
                            {isLoading && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                            Create Account
                        </Button>
                    </form>

                    <p className="text-sm text-slate-500 text-center mt-6">
                        Already have an account?{' '}
                        <Link to="/login" className="text-slate-900 font-medium hover:underline">
                            Sign in
                        </Link>
                    </p>
                </div>
            </div>
        </div>
    );
}

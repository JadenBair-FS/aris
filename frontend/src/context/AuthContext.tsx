import { createContext, useContext, useEffect } from 'react'
import type { ReactNode } from 'react'
import { useUser, useClerk, useAuth as useClerkAuth } from '@clerk/clerk-react'
import { setTokenGetter, setSignOut } from '../api/client'

export type UserRole = 'seeker' | 'recruiter'

export interface User {
    id: string
    name: string
    email: string
    role: UserRole
    avatarUrl?: string
}

interface AuthContextType {
    user: User | null
    isLoading: boolean
    login: (user: User) => void
    logout: () => void
}

const AuthContext = createContext<AuthContextType | undefined>(undefined)

export function AuthProvider({ children }: { children: ReactNode }) {
    const { user: clerkUser, isLoaded } = useUser()
    const { signOut } = useClerk()
    const { getToken } = useClerkAuth()

    useEffect(() => {
        setTokenGetter(getToken)
        setSignOut(signOut)
    }, [getToken, signOut])

    const user: User | null = clerkUser && isLoaded
        ? {
            id: clerkUser.id,
            name: clerkUser.fullName ?? clerkUser.primaryEmailAddress?.emailAddress ?? '',
            email: clerkUser.primaryEmailAddress?.emailAddress ?? '',
            role: (clerkUser.publicMetadata?.role as UserRole) ?? 'seeker',
            avatarUrl: clerkUser.imageUrl,
        }
        : null

    const login = () => {
        // No-op for now, login is handled by Clerk components/hooks directly in Login.tsx
        console.warn("login() called on AuthContext but auth is managed by Clerk.");
    };

    const logout = () => {
        signOut();
    };

    return (
        <AuthContext.Provider value={{ user, isLoading: !isLoaded, login, logout }}>
            {children}
        </AuthContext.Provider>
    )
}

export function useAuth() {
    const context = useContext(AuthContext)
    if (context === undefined) {
        throw new Error('useAuth must be used within an AuthProvider')
    }
    return context
}

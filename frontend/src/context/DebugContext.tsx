import { createContext, useContext, useState, useEffect } from 'react';
import type { ReactNode } from 'react';

export interface DebugLog {
    label: string;
    request: any;
    response: any;
    elapsedMs: number;
}

interface DebugContextType {
    logs: DebugLog[];
    addLog: (log: DebugLog) => void;
    clearLogs: () => void;
    isDebugMode: boolean;
}

const DebugContext = createContext<DebugContextType | undefined>(undefined);

export function DebugProvider({ children }: { children: ReactNode }) {
    const [logs, setLogs] = useState<DebugLog[]>([]);
    const [isDebugMode, setIsDebugMode] = useState(false);

    useEffect(() => {
        // Check session storage on mount
        const saved = sessionStorage.getItem('aris_debug');
        if (saved === 'true') {
            setIsDebugMode(true);
        }

        const handleKeyDown = (e: KeyboardEvent) => {
            if (e.ctrlKey && e.key.toLowerCase() === 'd') {
                e.preventDefault();
                setIsDebugMode(prev => {
                    const next = !prev;
                    sessionStorage.setItem('aris_debug', String(next));
                    return next;
                });
            }
        };

        const handleLogEvent = (e: Event) => {
            const customEvent = e as CustomEvent<DebugLog>;
            setLogs(prev => [...prev, customEvent.detail]);
        };

        window.addEventListener('keydown', handleKeyDown);
        window.addEventListener('aris-debug-log', handleLogEvent);
        return () => {
            window.removeEventListener('keydown', handleKeyDown);
            window.removeEventListener('aris-debug-log', handleLogEvent);
        };
    }, []);

    const addLog = (log: DebugLog) => {
        setLogs(prev => [...prev, log]);
    };

    const clearLogs = () => setLogs([]);

    return (
        <DebugContext.Provider value={{ logs, addLog, clearLogs, isDebugMode }}>
            {children}
            {isDebugMode && (
                <div className="fixed bottom-4 right-4 bg-slate-800 text-white text-xs px-2 py-1 rounded-md opacity-50 z-50 pointer-events-none">
                    DEBUG
                </div>
            )}
        </DebugContext.Provider>
    );
}

export function useDebug() {
    const context = useContext(DebugContext);
    if (context === undefined) {
        throw new Error('useDebug must be used within a DebugProvider');
    }
    return context;
}

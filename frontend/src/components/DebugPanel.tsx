import { useDebug } from '../context/DebugContext';
import { Tabs, TabsList, TabsTrigger, TabsContent } from './ui/tabs';

export function DebugPanel() {
    const { logs, isDebugMode } = useDebug();

    if (!isDebugMode || logs.length === 0) return null;

    return (
        <div className="fixed bottom-0 left-0 right-0 h-[50vh] bg-slate-950 text-slate-50 border-t-2 border-slate-700 shadow-[0_-10px_40px_-15px_rgba(0,0,0,0.5)] flex flex-col z-50 transition-all">
            <div className="p-3 border-b border-slate-800 flex justify-between items-center bg-slate-900 shrink-0">
                <span className="text-sm font-mono text-slate-300 uppercase tracking-widest font-bold">ARIS Network Debug ({logs.length} Requests)</span>
            </div>
            <div className="flex-1 overflow-hidden">
                <Tabs defaultValue="log-0" className="w-full h-full flex flex-col">
                    <TabsList className="w-full justify-start overflow-x-auto bg-slate-900 border-b border-slate-800 text-slate-400 flex-nowrap rounded-none h-12 px-2 shrink-0">
                        {logs.map((log, i) => (
                            <TabsTrigger key={i} value={`log-${i}`} className="data-[state=active]:bg-slate-800 data-[state=active]:text-white shrink-0 px-4 py-2 text-xs font-medium">
                                {log.label} ({log.elapsedMs}ms)
                            </TabsTrigger>
                        ))}
                    </TabsList>
                    <div className="flex-1 overflow-auto bg-[#0a0f1c]">
                        {logs.map((log, i) => (
                            <TabsContent key={i} value={`log-${i}`} className="h-full m-0 p-4 font-mono text-sm overflow-auto focus-visible:outline-none">
                                <div className="mb-6">
                                    <div className="text-slate-400 text-xs uppercase font-bold tracking-wider mb-2 border-b border-slate-800 pb-1">Request Payload</div>
                                    <pre className="text-blue-300 whitespace-pre-wrap bg-slate-900/50 p-4 rounded-md border border-slate-800">{JSON.stringify(log.request, null, 2)}</pre>
                                </div>
                                <div>
                                    <div className="text-slate-400 text-xs uppercase font-bold tracking-wider mb-2 border-b border-slate-800 pb-1">Response Payload</div>
                                    <pre className="text-emerald-300 whitespace-pre-wrap bg-slate-900/50 p-4 rounded-md border border-slate-800">{JSON.stringify(log.response, null, 2)}</pre>
                                </div>
                            </TabsContent>
                        ))}
                    </div>
                </Tabs>
            </div>
        </div>
    );
}

import { useState, useRef } from 'react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { useNavigate } from 'react-router';
import { Upload as UploadIcon } from 'lucide-react';
import { resumeApi } from '@/api/resume';
import { useMutation } from '@tanstack/react-query';
import { UploadProgress, RESUME_PHASES } from '@/components/UploadProgress';

export default function Upload() {
    const [file, setFile] = useState<File | null>(null);
    const [text, setText] = useState('');
    const [errorText, setErrorText] = useState('');
    const [isDragging, setIsDragging] = useState(false);
    const fileInputRef = useRef<HTMLInputElement>(null);
    const navigate = useNavigate();

    const uploadPdfMutation = useMutation({
        mutationFn: (f: File) => resumeApi.uploadPdf(f),
        onSuccess: () => navigate('/profile'),
        onError: (err: any) => setErrorText(err.message),
    });

    const uploadTextMutation = useMutation({
        mutationFn: (content: string) => resumeApi.uploadText(content),
        onSuccess: () => navigate('/profile'),
        onError: (err: any) => setErrorText(err.message),
    });

    const isPending = uploadPdfMutation.isPending || uploadTextMutation.isPending;

    const handlePdfSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setErrorText('');
        if (!file) return setErrorText('Please select a PDF file.');
        uploadPdfMutation.mutate(file);
    };

    const handleTextSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setErrorText('');
        if (!text.trim()) return setErrorText('Please paste your resume text.');
        uploadTextMutation.mutate(text);
    };

    const handleDrop = (e: React.DragEvent) => {
        e.preventDefault();
        setIsDragging(false);
        const dropped = e.dataTransfer.files[0];
        if (dropped?.type === 'application/pdf') {
            setFile(dropped);
        } else {
            setErrorText('Only PDF files are supported.');
        }
    };

    return (
        <div className="max-w-xl mx-auto space-y-6">
            <UploadProgress isVisible={isPending} phases={RESUME_PHASES} />

            <div>
                <h1 className="text-2xl font-semibold text-slate-900">Upload Resume</h1>
                <p className="text-slate-500 text-sm mt-1">We'll extract your skills and map them to the knowledge graph.</p>
            </div>

            {errorText && (
                <Alert variant="destructive">
                    <AlertDescription>{errorText}</AlertDescription>
                </Alert>
            )}

            <Card>
                <CardHeader>
                    <CardTitle className="text-base">Resume Input</CardTitle>
                    <CardDescription>Uploading a new resume will replace your existing profile.</CardDescription>
                </CardHeader>
                <CardContent>
                    <Tabs defaultValue="pdf">
                        <TabsList className="grid w-full grid-cols-2 mb-6">
                            <TabsTrigger value="pdf">PDF File</TabsTrigger>
                            <TabsTrigger value="text">Paste Text</TabsTrigger>
                        </TabsList>

                        <TabsContent value="pdf">
                            <form onSubmit={handlePdfSubmit} className="space-y-4">
                                {/* Drag-drop zone */}
                                <div
                                    onClick={() => fileInputRef.current?.click()}
                                    onDragOver={e => { e.preventDefault(); setIsDragging(true); }}
                                    onDragLeave={() => setIsDragging(false)}
                                    onDrop={handleDrop}
                                    className={`border-2 border-dashed rounded-lg p-8 text-center cursor-pointer transition-colors ${
                                        isDragging
                                            ? 'border-slate-400 bg-slate-50'
                                            : 'border-slate-200 hover:border-slate-300'
                                    }`}
                                >
                                    <UploadIcon className="h-8 w-8 text-slate-400 mx-auto mb-3" />
                                    {file ? (
                                        <p className="text-sm font-medium text-slate-700">{file.name}</p>
                                    ) : (
                                        <>
                                            <p className="text-sm font-medium text-slate-700">Drop your PDF here</p>
                                            <p className="text-xs text-slate-400 mt-1">or click to browse</p>
                                        </>
                                    )}
                                </div>
                                <input
                                    ref={fileInputRef}
                                    type="file"
                                    accept=".pdf"
                                    className="hidden"
                                    onChange={e => setFile(e.target.files?.[0] || null)}
                                    disabled={isPending}
                                />
                                <Button type="submit" className="w-full bg-slate-900 hover:bg-slate-800 text-white" disabled={isPending || !file}>
                                    Analyze Resume
                                </Button>
                            </form>
                        </TabsContent>

                        <TabsContent value="text">
                            <form onSubmit={handleTextSubmit} className="space-y-4">
                                <Textarea
                                    className="min-h-72 text-sm"
                                    value={text}
                                    onChange={e => setText(e.target.value)}
                                    placeholder="Paste your full resume text here..."
                                    disabled={isPending}
                                />
                                <Button type="submit" className="w-full bg-slate-900 hover:bg-slate-800 text-white" disabled={isPending || !text.trim()}>
                                    Analyze Resume
                                </Button>
                            </form>
                        </TabsContent>
                    </Tabs>
                </CardContent>
            </Card>
        </div>
    );
}

export interface StudyCompareResponse {
  ragResponse: string;
  graphRagResponse: string;
  tierSummary: {
    tier1Count: number;
    tier2Count: number;
    tier3Count: number;
    tier4Count: number;
    vectorSimilarity: number;
  };
}

export interface StudyTailorResponse {
  tailoredText: string;
}

export async function prepareResume(text: string): Promise<{ sessionKey: string }> {
  const res = await fetch('/api/study/prepare-resume', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to process resume.');
  }
  return res.json();
}

export async function prepareJob(text: string): Promise<{ sessionKey: string }> {
  const res = await fetch('/api/study/prepare-job', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to process job description.');
  }
  return res.json();
}

export async function generateComparison(
  resumeKey: string,
  jobKey: string,
  resumeText: string,
  jobDescriptionText: string
): Promise<StudyCompareResponse> {
  const res = await fetch('/api/study/generate-comparison', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeKey, jobKey, resumeText, jobDescriptionText }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({}));
    throw new Error((body as { error?: string }).error ?? 'Failed to generate responses.');
  }
  return res.json();
}

export async function tailorResume(
  resumeText: string,
  jobDescriptionText: string,
  resumeKey?: string,
  jobKey?: string
): Promise<StudyTailorResponse> {
  const res = await fetch('/api/study/tailor', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeText, jobDescriptionText, resumeKey, jobKey }),
  });
  if (!res.ok) throw new Error('Failed to generate tailored resume');
  return res.json();
}

// Legacy single-call compare (kept for compatibility)
export async function compareResponses(
  resumeText: string,
  jobDescriptionText: string
): Promise<StudyCompareResponse> {
  const res = await fetch('/api/study/compare', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeText, jobDescriptionText }),
  });
  if (!res.ok) throw new Error('Failed to generate responses');
  return res.json();
}

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

export async function tailorResume(
  resumeText: string,
  jobDescriptionText: string
): Promise<StudyTailorResponse> {
  const res = await fetch('/api/study/tailor', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ resumeText, jobDescriptionText }),
  });
  if (!res.ok) throw new Error('Failed to generate tailored resume');
  return res.json();
}

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

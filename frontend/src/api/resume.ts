import { apiClient, fetchBlobWithAuth } from './client';
import type { UserProfileDetail, MatchAnalysisResult, ResumeUploadResult, GroundingCorrection } from '../types/api';

function buildTierPayload(analysis: MatchAnalysisResult) {
    return {
        matchingSkills: analysis.matchingSkills.map(s => s.skillName),
        implicitSkills: analysis.implicitlyDiscoveredSkills.map(s => s.skillName),
        prereqMetSkills: analysis.prerequisiteMetSkills.map(s => s.skillName),
        bridgeableSkills: analysis.bridgeableSkills.map(s => s.skillName),
        hardGaps: analysis.hardGaps.map(s => s.skillName),
    };
}

export const resumeApi = {
    uploadPdf: (file: File) => {
        const formData = new FormData();
        formData.append('file', file);
        return apiClient<ResumeUploadResult>('/resume/upload', {
            method: 'POST',
            body: formData,
        });
    },
    uploadText: (content: string) => {
        return apiClient<ResumeUploadResult>('/resume/upload-text', {
            method: 'POST',
            body: JSON.stringify({ content }),
        });
    },
    applyGrounding: (profileId: string, corrections: GroundingCorrection[]) =>
        apiClient<{ message: string }>(`/resume/${profileId}/grounding`, {
            method: 'PATCH',
            body: JSON.stringify({ corrections }),
        }),
    getProfileByUserId: (clerkId: string) => {
        return apiClient<UserProfileDetail>(`/resume/by-user/${clerkId}`);
    },
    getProfile: (id: string) => {
        return apiClient<UserProfileDetail>(`/resume/${id}`);
    },
    deleteResume: () => {
        return apiClient<{ message: string }>('/resume', { method: 'DELETE' });
    },
    tailorPdf: async (userProfileId: string, jobId: string, analysis?: MatchAnalysisResult): Promise<Blob> => {
        const tierPayload = analysis ? buildTierPayload(analysis) : {};
        const response = await fetchBlobWithAuth('/resume/tailor-pdf', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId, ...tierPayload }),
        });
        if (!response.ok) {
            const text = await response.text();
            throw new Error(text || `PDF generation failed with status ${response.status}`);
        }
        return response.blob();
    },
};

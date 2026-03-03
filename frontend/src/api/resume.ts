import { apiClient } from './client';
import type { UserProfileDetail, TailoredBullet } from '../types/api';

export const resumeApi = {
    uploadPdf: (file: File) => {
        const formData = new FormData();
        formData.append('file', file);
        return apiClient<{ message: string; id: string }>('/resume/upload', {
            method: 'POST',
            body: formData,
        });
    },
    uploadText: (content: string) => {
        return apiClient<{ message: string; id: string }>('/resume/upload-text', {
            method: 'POST',
            body: JSON.stringify({ content }),
        });
    },
    getProfileByUserId: (clerkId: string) => {
        return apiClient<UserProfileDetail>(`/resume/by-user/${clerkId}`);
    },
    getProfile: (id: string) => {
        return apiClient<UserProfileDetail>(`/resume/${id}`);
    },
    tailor: (userProfileId: string, jobId: string) => {
        return apiClient<TailoredBullet[]>('/resume/tailor', {
            method: 'POST',
            body: JSON.stringify({ userProfileId, jobId }),
        });
    },
};

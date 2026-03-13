FROM node:20-alpine AS build
WORKDIR /app

# Accept build arguments
ARG VITE_CLERK_PUBLISHABLE_KEY
ENV VITE_CLERK_PUBLISHABLE_KEY=$VITE_CLERK_PUBLISHABLE_KEY

# Copy package files first
COPY package.json package-lock.json ./

# Use install instead of ci for better resilience during migration
RUN npm install

# Copy the rest of the application
COPY . .

# Build the frontend
RUN npm run build

# Serve with Nginx
FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]

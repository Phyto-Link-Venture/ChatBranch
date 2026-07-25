FROM node:20-alpine

WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .

# NEXT_PUBLIC_* are baked into the client bundle at build time. Railway only
# passes service variables into Docker builds as declared ARGs.
ARG NEXT_PUBLIC_API_URL
ARG NEXT_PUBLIC_MAX_CONCURRENT
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL \
    NEXT_PUBLIC_MAX_CONCURRENT=$NEXT_PUBLIC_MAX_CONCURRENT

RUN npm run build

EXPOSE 3000

CMD ["npm", "start"]

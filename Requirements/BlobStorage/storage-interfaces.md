# Storage Interfaces

## Overview

The system must expose industry-standard object storage interfaces so that existing tools, SDKs, and client applications can interact with it without modification.

## Requirements

### S3-Compatible Interface
- Implement the AWS S3 REST API surface covering core operations: `PutObject`, `GetObject`, `DeleteObject`, `ListObjects`, `CreateBucket`, `DeleteBucket`, `HeadObject`, `HeadBucket`, `CopyObject`.
- Support multipart upload (`CreateMultipartUpload`, `UploadPart`, `CompleteMultipartUpload`, `AbortMultipartUpload`).
- Support range requests (`Range` header on `GetObject`) for partial retrieval of large blobs.
- Return S3-compatible XML responses and HTTP status codes so existing S3 SDKs work without modification.
- Support presigned URLs for time-limited, unauthenticated access to specific objects.

### Azure Blob Storage–Compatible Interface
- Implement the Azure Blob Storage REST API surface covering core operations: `Put Blob`, `Get Blob`, `Delete Blob`, `List Blobs`, `Create Container`, `Delete Container`, `Get Blob Properties`.
- Support block blob upload model (Put Block, Put Block List) for large file ingestion.
- Support shared access signature (SAS) tokens as the Azure equivalent of presigned URLs.
- Return Azure-compatible XML/JSON responses and HTTP status codes so existing Azure SDKs work without modification.

### SFTP Interface

- Expose an SFTP server endpoint that maps to a tenant's blob storage bucket, allowing clients that only support file transfer via SFTP (common in enterprise retail, logistics, and data exchange workflows) to read and write blobs without using the S3 or Azure REST APIs.
- Each tenant is issued SFTP credentials (username + SSH public key authentication) scoped to their own bucket. A tenant cannot traverse to another tenant's storage.
- The SFTP directory tree maps directly to the blob key namespace: the top-level SFTP directory corresponds to the bucket root; subdirectories correspond to key prefixes.
- Files written via SFTP are stored as standard blobs and are accessible via the S3 and Azure interfaces immediately after the SFTP session closes.
- Files written via SFTP trigger the normal `blob.created` / `blob.updated` event notifications (see [Blob Event Notifications](./blob-event-notifications.md)), enabling downstream processors (e.g., the product catalog feed processor) to react to SFTP-delivered files without special handling.
- SFTP reads correspond to `GetObject`; SFTP writes correspond to `PutObject`; directory listings correspond to `ListObjects`. Delete via SFTP follows the same soft/hard delete policy as API deletes.
- Resume / partial transfer: SFTP's native resume semantics are supported for interrupted uploads of large files.
- The SFTP interface is optional and can be enabled or disabled per tenant and per node configuration.

### Co-existence
- Both the S3 interface and Azure Blob interface may be enabled simultaneously on a single node instance.
- Each interface listens on its own configurable port or path prefix to avoid routing ambiguity.
- A single blob stored via one interface is accessible via the other interface using equivalent addressing (bucket/container + key mapping).
- Neither interface is mandatory; each can be individually enabled or disabled via node configuration.

## Non-Goals
- Full feature parity with every edge-case or advanced feature of either cloud provider (e.g., S3 Object Lock legal holds, Azure immutability policies) is out of scope for the initial version but the architecture should not foreclose adding them.

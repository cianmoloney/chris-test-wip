You are an exeprt an experienced system architecture and software engineer. 

I want you to scan and understand the solution as a whole, and assess it against the following requirements. Note in any gaps and report on the plan on how to fix them. 

The system, is a HR system that keeps track of workers in a construction company, their documents, and their eligibility to work. It will be used by Foreman (on site) as well as HR and Admin staff (in the main office).


=========================
**1. High Level System**
=========================
We're using an Azure stack to do this, and will require a SQL database, an Azure Function, and a Web App Frontend. Supporting resources will be an Azure Storage Account, Azure Communications Services (for MFA) and Azure Computer Vision (for AI image processing). 

The SQL database will hold backend data. This database will need to be exposed by an API. For now, let's use HTTP triggers in the Azure Function to do this. The Azure function will also have some background processing tasks (e.g processing Images). The storage account will be used to store files uploaded by users of the system.

The database, and storage account should have encryption at rest & in transit enabled,

The website and functions http triggers should be secure. 

Each service will talk to eachother using managed identity only, with their being a specific SQL admin role configured for direct access if needed. 

Components are deployed through VisualStudio - no provisioning scripts needed.



==================================================
**2. System Actors and Entities**
==================================================
There are three types of core [Users] - a member of HR, a FOREMAN, and an ADMIN. These people would be maintained in the [Users] table. Users can be assigned a [Roles], and in turn that [UserRoles] has [UserResponsibilities]. These will be used to determine what a [Users] can/cannot do, and what they can/cannot see on the front end.

In addition, another type of user, but they are the site workers for the company. They will be tracked in a separate table called [Staff], which will have a [Types] as we will need to differentiate between Permanent and Contract [Staff]. There will also be a [StaffRole], which can be something like "Forklift Driver" or "Brick Layer" for example. 

[Staff] will require [Documents] which are generally certificates which entitle them to be on site. [Documents] will have a [Type] and other metadata such as [Name] (if it's a certificate) as well as [StartDate] and [EndDate]. [Staff] can have many of the same [DocumentType], as we keep a record of what documents they had historically. There's a relationship between [StaffRole] and [DocumentType] as certain roles require certain [DocumentTypes].

There's also the concept of [Terms]. The company will occasionally issue documents (of text) which could be something like "terms and conditions" or "liability document" which some [Staff] will need to sign off on. [Terms] can be updated, so one type of [Terms] can have multiple [TermVersions], which can contain the contents of the terms. Some [Staff] don't speak English, so it will need to persist [TermVersions] in several languages. 

We'll need to keep a record, of which [Staff] accepted which [Terms], including version and language, at what date. [Staff] will only have to click accept, which will prove that the [Terms] are agreed to.

A member of [Staff] is only Ready for work if they have signed all [Terms] allocated to them, as well as having all [Documents] validated for their [StaffRole].



==================================================
**3. Actor Functionality**
==================================================

*HR*
The HR  [Users], will login with their email and password. They can optionally signed up for MFA, in which case they'll have to input the code sent to their email. 

They can View/Add/Remove/Edit [Staff] records. They can upload [Documents] to storage and assign them to [Staff]. They can View/Add/Remove/Edit [Document] records such as the [StartDate], in the event that a [Staff] member has added them incorrectly, or the system has populated the field incorrectly. 

When viewing [Staff], they can filter and sort them. It should be easy for them to Navigate to the [Staff] page, to an individual [Staff] record, where they can see the record in detail, along with associated documents and terms. 

When viewing [Documents] they can also filter and sort them. It should also be easy to Navigate to the [Staff] associated with a document. They can also manually upload and download files. They can (re)associate files to different workers, if they were misallocated to begin with. 

In addition to this - the HR [Users] will be able to check the "Valid" checklist for all documents. This will ensure that the auto ingestion of the [Document] by the Azure Function has done it's job correctly. All [Documents] should be validated before being classified as Valid in the table bool.

The HR [Users] can generate and issue FOUR types of separate secure links to send to [Staff]. For now, the links are just generated for them to copy, and not automatically emailed. Some links are assigned to a specific [Staff] only, and one are not assigned to any [Staff] in particular. The link should be valid for a set period of time. The different types are "Registration", "Multiple Registration", "Documents", and "Terms".

A [Registration] link is not assigned to any [Staff]. It allows for the induction of new [Staff]. When shared, this will allow that person to Register via the "Registration" page. Once Registration is successful, the same link cannot be used again. 

A [Multiple Registration] link is not assigned to any [Staff]. It allows for the induction of new multiple [Staff] at onec. It's the same functionality as the existing registration, just that there's an "Add another.." button which will allow one person to add several [Staff] at once. Once Registration is successful, the same link cannot be used again. 

A "Documents" link can be for [Staff] or generic. For [Staff], the HR [Users] can select the [Staff] member, or leave it blank to have a generic link. The link takes the person to the "Upload Documents" page. If the [Staff] link is used, the [Staff] identifier is retrieved from the database, and prepended as "SID{number}" as part of the document file name, and uploaded to Blob storage. If a generic link is sent, it's simply uploaded into Blob storage. All blobs uploaded are done by date. The will be uploaded into a YYYY container format with the month MM also in the blob path. Once the Document is uploaded (to blob storage), it cannot be used again. 

For the "Terms" link, the HR [Users] will be required to select the [Staff] and the [Terms], and generate the link. The [Staff] who clicks the link will be taken to the "Terms" page, and will be allowed to choose their language, and read and click "Accept". The "Acceptance" and date in UTC will be kept as described earlier.

The HR [Users] can also Add/Remove/Edit [Users], assigning them different Roles, except for Admin. 

*STAFF*
For [Staff], they access the system via a secure link, issued by the system, or by the HR User / Foreman / Admin. There should be a translation feature to the page for POLISH and UKRAINIAN, as many are foreign workers. [Staff] will have three pages they can access - "Registration", "File upload", and "Terms". 

When [Staff] accesses the registration page, they input personal data which is persisted to a SQL database. 

When [Staff] access the file upload page, they can choose from the page what [DocumentType] they are uploading, and upload files from their device. This will be uploaded to Blob Storage. 
If they select a [DocumentType], the DTID_{number} is prepended to the Blob filename, along with the [Staff] SID mentioned earlier. If no [DocumentType] is selected, it's ommitted from the FileName.  There should be a 1:many relationship between [Staff] and [Document], as they can replace their certificates with new ones then required.


When [Staff] access the "Terms" page, they'll be shown text regarding their role, to which they have to tick "agree", which is then persisted in the database along with the terms version, and time in UTC. There should be a many:many relationship between a [Staff] and [Terms], as there might be several different terms they have to accept over the course of their role. Every time they accept terms, there's a record of what the terms were and when they were accepted.

*FOREMAN*
The Foreman [Users], will login with their email and password. They will not require MFA, which is optional, but disabled by default. 

The Foreman [Users] can easily generate the same links the the HR User can do, and in the same way. 

The Foreman [Users] can also View [Staff] information, most importantly, viewing their [Documents], and making sure that they are able to work. The Foreman [Users] can also perform the same Validation check that the HR [Users] can do.


*ADMIN*
The Admin [Users] will login with their username and password, MFA optional. The Admin can do everything that the HR User can so. 

The Admin [Users] will have a [Roles] and [Resonsibilities] view, allowing them to assign [Responsibilies] to roles (and therefore making more parts of the site accessible to different roles).

There will be some added "admin" actions listed in the future, but for now, they have the same responsibilities.



==================================================
**4. Added System Functionality**
==================================================
The function will run a daily check to see which [Documents] are going to expire in the next week. Results are collated and sent to the HR [Users] over email. The email contains a list [Staff], the [DocumentType] and [EndDate]. If the [Staff] has a replacement [Document] that has been verified in the same list, then the expiring [Document] will not be flagged.

When the [Document] has been uploaded to Blob storage, the Azure Function (with a bob trigger) will process the file. The function will pull the [Staff] ID and [DocumentType] ID if they are present in the FileName. The [Document] itself could could be a pdf or image. If it's a pdf and we cannot pull any data from it, then it will be retried as an image. The service will validate that it is not invalid (has malicious code injection logic). It will use Azure Computer Vision to then pull out basic information - [DocumentType], and details from the [Document] table - the name of the person, phone number, email, the start  / end dates, the document number. We will parse the file and pull data from it, inserting it into SQL. The file will then be flagged for review, for the HR [Users], Admin [Users] or Foreman [Users], to validate its credibility. If the file cannot be parsed, it will also be flagged as having an issue. If possible, the [Document] will be associated with the [Staff] and [Document]. 
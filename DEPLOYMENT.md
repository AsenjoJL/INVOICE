# HazelInvoice Deployment Guide (Render + MySQL)

This guide walks you through deploying the **HazelInvoice** ASP.NET Core 8.0 application to **Render.com** (Free Tier) with a hosted MySQL database.

## Prerequisites
1.  **GitHub Account**: You must have a GitHub repository for this project.
2.  **Render Account**: Sign up at [dashboard.render.com](https://dashboard.render.com/).
3.  **MySQL Database**: You need a hosted MySQL database.
    *   **Option A (Recommended for Free)**: [Aiven.io](https://aiven.io/) (Free MySQL plan).
    *   **Option B**: [PlanetScale](https://planetscale.com/) (If available).
    *   **Option C**: [Clever Cloud](https://www.clever-cloud.com/) (Free MySQL tier).

---

## Step 1: Push Code to GitHub
1.  Initialize Git if you haven't:
    ```bash
    git init
    git add .
    git commit -m "Prepare for deployment"
    ```
2.  Create a new repository on GitHub.
3.  Push your code:
    ```bash
    git remote add origin https://github.com/YOUR_USERNAME/HazelInvoice.git
    git branch -M main
    git push -u origin main
    ```

---

## Step 2: Create MySQL Database
1.  Go to your chosen database provider (e.g., Aiven or Clever Cloud).
2.  Create a new **MySQL** service (select the Free tier).
3.  Copy the **Connection String** (Service URI). It usually looks like:
    `mysql://user:password@host:port/defaultdb?ssl-mode=REQUIRED`
4.  **Important**: Convert this into a standard .NET Connection String format if needed, or use the URI directly if compatible. .NET format:
    `Server=HOST;Port=PORT;Database=DB_NAME;User=USERNAME;Password=PASSWORD;`

---

## Step 3: Deploy to Render
1.  Log in to [Render Dashboard](https://dashboard.render.com/).
2.  Click **New +** -> **Web Service**.
3.  Select **Build and deploy from a Git repository** and connect your `HazelInvoice` repo.
4.  **Configure the Service**:
    *   **Name**: `hazel-invoice` (or unique name).
    *   **Region**: Singapore (or closest to you).
    *   **Runtime**: **Docker** (Render will detect the `Dockerfile`).
    *   **Instance Type**: **Free**.
5.  **Environment Variables** (Critical Step):
    Scroll down to "Environment Variables" and add:
    
    | Key | Value |
    | --- | --- |
    | `ASPNETCORE_ENVIRONMENT` | `Production` |
    | `ConnectionStrings__DefaultConnection` | `Server=YOUR_HOST;Port=YOUR_PORT;Database=YOUR_DB;User=YOUR_USER;Password=YOUR_PASSWORD;` |
    
    *Note: `ConnectionStrings__DefaultConnection` (double underscore) overrides dependencies in `appsettings.json`.*

6.  Click **Create Web Service**.

---

## Step 4: Verify Deployment
1.  Render will start building your Docker image. This may take 3-5 minutes.
2.  Once built, it will deploy.
3.  **Automatic Migration**: On startup, the app will run `db.Database.MigrateAsync()` (as configured in `DbInitializer.cs`), which will:
    *   Create the database tables automatically.
    *   Seed the initial data (Customers, Products) if empty.
4.  Visit your URL (`https://hazel-invoice.onrender.com`).
5.  Login with your credentials (or register if setup allows).

## Troubleshooting
*   **Database Connection Error**: Check your Connection String in Render Environment Variables. Ensure the firewall on your Database provider allows connections from anywhere (0.0.0.0/0) or Render's IP.
*   **Build Failures**: Check the "Logs" tab in Render for Docker errors.

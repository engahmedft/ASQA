#!/usr/bin/env bash
set -e

PUBLISH_DIR="./publish"
STAGING_DIR="./publish_temp"

echo "=================================================================="
echo " 📦 ASQA IIS PUBLISH WIZARD"
echo "=================================================================="

MODE="$1"
if [ "$MODE" != "1" ] && [ "$MODE" != "2" ]; then
    echo "Please select your deployment mode:"
    echo "  [1] Full Self-Contained Deployment (First-Time Setup, ~110MB)"
    echo "  [2] Fast Delta Deploy (App & UI Updates Only, ~15MB)"
    read -p "Enter choice [1 or 2]: " MODE
fi

rm -rf "$PUBLISH_DIR" "$STAGING_DIR"
mkdir -p "$PUBLISH_DIR"

if [ "$MODE" == "1" ]; then
    echo "🚀 Building [Option 1: Full Self-Contained Deployment]..."
    dotnet publish ASQATools/ASQATools.csproj -c Release -r win-x64 --self-contained true -o "$PUBLISH_DIR"
    rm -f "$PUBLISH_DIR"/createdump.exe

    cat << 'WCFG' > "$PUBLISH_DIR/web.config"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <clear />
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath=".\ASQATools.exe" arguments="" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
WCFG

    mkdir -p "$PUBLISH_DIR/Templates"
    cp -rf ASQATools/Templates/* "$PUBLISH_DIR/Templates/"
    rm -rf "$PUBLISH_DIR/pr_reviews_data"

elif [ "$MODE" == "2" ]; then
    echo "⚡ Building [Option 2: Fast Delta Deploy (App & UI Only)]..."
    dotnet publish ASQATools/ASQATools.csproj -c Release -r win-x64 --self-contained true -o "$STAGING_DIR"

    cp -f "$STAGING_DIR"/ASQATools.dll "$PUBLISH_DIR/"
    cp -f "$STAGING_DIR"/ASQATools.Shared.dll "$PUBLISH_DIR/"
    cp -f "$STAGING_DIR"/ASQATools.Client.dll "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.pdb "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.Shared.pdb "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.Client.pdb "$PUBLISH_DIR/" 2>/dev/null || true
    cp -f "$STAGING_DIR"/ASQATools.deps.json "$PUBLISH_DIR/"
    cp -rf "$STAGING_DIR"/wwwroot "$PUBLISH_DIR/"
    mkdir -p "$PUBLISH_DIR/Templates"
    cp -rf ASQATools/Templates/* "$PUBLISH_DIR/Templates/"
    rm -rf "$STAGING_DIR"
fi

echo "=================================================================="
echo " ✔ Publish complete in: $(pwd)/publish"
echo " (Your project files remain configured for development without changes)"
echo "=================================================================="

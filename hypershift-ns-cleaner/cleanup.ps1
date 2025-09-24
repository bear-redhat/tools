#!/usr/bin/env pwsh

<#
.SYNOPSIS
    HyperShift Namespace Cleaner - Removes finalizers from cluster.cluster.x-k8s.io resources in terminating namespaces

.DESCRIPTION
    This script identifies namespaces stuck in "Terminating" status and removes finalizers from 
    cluster.cluster.x-k8s.io resources within those namespaces to help them complete deletion.

.PARAMETER Context
    OpenShift context to use (default: "hosted-mgmt")

.PARAMETER DryRun
    Run in dry-run mode to show what would be done without making changes

.EXAMPLE
    .\cleanup.ps1
    Run with default context in live mode

.EXAMPLE
    .\cleanup.ps1 -Context "my-cluster" -DryRun
    Run with specific context in dry-run mode
#>

param(
    [Parameter(Mandatory = $false)]
    [string]$Context = "hosted-mgmt",
    
    [Parameter(Mandatory = $false)]
    [switch]$DryRun = $false
)

function Test-OcCommand {
    try {
        $null = Get-Command oc -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Test-Context {
    param([string]$ContextName)
    
    try {
        $contexts = oc config get-contexts -o name 2>$null
        return $contexts -contains $ContextName
    }
    catch {
        return $false
    }
}

try {
    Write-Host "Checking for terminating namespaces..." -ForegroundColor Yellow
    Write-Host "Context: $Context" -ForegroundColor Cyan
    if ($DryRun) {
        Write-Host "Mode: DRY RUN (no changes will be made)" -ForegroundColor Magenta
    } else {
        Write-Host "Mode: LIVE (finalizers will be removed)" -ForegroundColor Yellow
    }
    Write-Host ("-" * 50)

    if (-not (Test-OcCommand)) {
        Write-Error "Error: 'oc' command not found. Please ensure OpenShift CLI is installed and in PATH."
        exit 1
    }

    if (-not (Test-Context -ContextName $Context)) {
        Write-Error "Error: Context '$Context' not found. Available contexts:"
        oc config get-contexts -o name
        exit 1
    }

    $terminatingNamespaces = oc --context="$Context" get namespaces -o json | 
        ConvertFrom-Json | 
        ForEach-Object { $_.items } | 
        Where-Object { $_.status.phase -eq "Terminating" }

    if ($terminatingNamespaces.Count -eq 0) {
        Write-Host "✓ No namespaces found in Terminating status." -ForegroundColor Green
    }
    else {
        Write-Host "Found $($terminatingNamespaces.Count) namespace(s) in Terminating status:" -ForegroundColor Red
        Write-Host ""
        
        $results = @()
        foreach ($ns in $terminatingNamespaces) {
            $deletionTime = if ($ns.metadata.deletionTimestamp) { 
                [DateTime]::Parse($ns.metadata.deletionTimestamp) 
            } else { 
                Get-Date 
            }
            $duration = (Get-Date) - $deletionTime
            
            $results += [PSCustomObject]@{
                Name = $ns.metadata.name
                Status = $ns.status.phase
                'Deletion Started' = $deletionTime.ToString("yyyy-MM-dd HH:mm:ss")
                'Duration (Terminating)' = "$($duration.Days)d $($duration.Hours)h $($duration.Minutes)m $($duration.Seconds)s"
                Finalizers = ($ns.metadata.finalizers -join ", ")
            }
        }
        
        $results | Format-Table -AutoSize
        
        # Process cluster.x-k8s.io resources in terminating namespaces
        Write-Host ""
        if ($DryRun) {
            Write-Host "DRY RUN: Showing cluster.x-k8s.io resources that would be processed..." -ForegroundColor Magenta
        } else {
            Write-Host "Processing cluster.x-k8s.io resources in terminating namespaces..." -ForegroundColor Yellow
        }
        Write-Host ("-" * 70)
        
        $processedCount = 0
        $errorCount = 0
        
        foreach ($ns in $terminatingNamespaces) {
            $nsName = $ns.metadata.name
            Write-Host "Checking namespace: $nsName" -ForegroundColor Cyan
            
            try {
                # Define resource types to check
                $resourceTypes = @(
                    "cluster.cluster.x-k8s.io",
                    "awsmachine.infrastructure.cluster.x-k8s.io",
                    "hostedcontrolplanes.hypershift.openshift.io",
                    "machinedeployments.cluster.x-k8s.io",
                    "machines.cluster.x-k8s.io",
                    "machinesets.cluster.x-k8s.io",
                    "ibmpowervsimage.infrastructure.cluster.x-k8s.io",
                    "ibmpowervsmachine.infrastructure.cluster.x-k8s.io"
                )
                
                foreach ($resourceType in $resourceTypes) {
                    try {
                        # Get resources of this type in this namespace
                        $resources = oc --context="$Context" get $resourceType -n $nsName -o json 2>$null | ConvertFrom-Json
                        
                        if ($resources.items -and $resources.items.Count -gt 0) {
                            Write-Host "  Found $($resources.items.Count) $resourceType resource(s)" -ForegroundColor Green
                            
                            foreach ($resource in $resources.items) {
                                $resourceName = $resource.metadata.name
                                
                                if ($resource.metadata.finalizers -and $resource.metadata.finalizers.Count -gt 0) {
                                    if ($DryRun) {
                                        Write-Host "    [DRY RUN] Would remove finalizers from $resourceType : $resourceName" -ForegroundColor Magenta
                                        Write-Host "    [DRY RUN] Current finalizers: $($resource.metadata.finalizers -join ', ')" -ForegroundColor Magenta
                                        $processedCount++
                                    } else {
                                        Write-Host "    Removing finalizers from $resourceType : $resourceName" -ForegroundColor Yellow
                                        
                                        try {
                                            # Remove finalizers by patching the resource
                                            $result = oc --context="$Context" --as system:admin patch $resourceType "$resourceName" -n "$nsName" --type=merge -p '{\"metadata\":{\"finalizers\":[]}}' 2>&1
                                            
                                            if ($LASTEXITCODE -eq 0) {
                                                Write-Host "    ✓ Successfully removed finalizers from $resourceName" -ForegroundColor Green
                                                $processedCount++
                                            } else {
                                                Write-Host "    ✗ Failed to remove finalizers from $resourceName : $result" -ForegroundColor Red
                                                $errorCount++
                                            }
                                        }
                                        catch {
                                            Write-Host "    ✗ Failed to remove finalizers from $resourceName : $($_.Exception.Message)" -ForegroundColor Red
                                            $errorCount++
                                        }
                                    }
                                }
                                else {
                                    Write-Host "    → $resourceType $resourceName has no finalizers" -ForegroundColor Gray
                                }
                            }
                        }
                        else {
                            Write-Host "  No $resourceType resources found" -ForegroundColor Gray
                        }
                    }
                    catch {
                        # Skip if resource type doesn't exist or other errors
                        Write-Host "  No $resourceType resources found (or error accessing)" -ForegroundColor Gray
                    }
                }
            }
            catch {
                Write-Host "  ✗ Error checking namespace $nsName : $($_.Exception.Message)" -ForegroundColor Red
                $errorCount++
            }
        }
        
        Write-Host ""
        Write-Host ("-" * 70)
        if ($DryRun) {
            Write-Host "Dry run summary:" -ForegroundColor Magenta
            Write-Host "  Would process: $processedCount resource(s)" -ForegroundColor Magenta
        } else {
            Write-Host "Finalizer removal summary:" -ForegroundColor Cyan
            Write-Host "  Successfully processed: $processedCount resource(s)" -ForegroundColor Green
        }
        if ($errorCount -gt 0) {
            Write-Host "  Errors encountered: $errorCount" -ForegroundColor Red
        }
    }
}
catch {
    Write-Error "Error occurred while checking namespaces: $($_.Exception.Message)"
    exit 1
}

Write-Host ""
Write-Host "Script completed." -ForegroundColor Green

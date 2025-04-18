#!/bin/bash

# AWS IAM Cleaner Script
# Removes IAM users and roles that haven't been active for more than specified days

# Default values
DAYS=2
DRY_RUN=false
LOG_FILE="aws-cleaner-$(date +%Y%m%d-%H%M%S).log"
DELAY=1       # Default delay between API calls in seconds
MAX_RETRIES=5 # Maximum number of retries for throttled API calls
PAGE_SIZE=20  # Default number of items per page for list operations

# Function to show usage information
usage() {
    echo "Usage: $0 [options]"
    echo "Options:"
    echo "  -d, --days DAYS    Number of days of inactivity (default: 2)"
    echo "  --dry-run          Run without making any changes"
    echo "  --delay SECONDS    Delay between API calls in seconds (default: 1)"
    echo "  --page-size SIZE   Maximum items per page for list operations (default: 20)"
    echo "  -h, --help         Show this help message"
    exit 1
}

# Parse command line arguments
while [[ "$#" -gt 0 ]]; do
    case $1 in
        -d|--days) DAYS="$2"; shift ;;
        --dry-run) DRY_RUN=true ;;
        --delay) DELAY="$2"; shift ;;
        --page-size) PAGE_SIZE="$2"; shift ;;
        -h|--help) usage ;;
        *) echo "Unknown parameter: $1"; usage ;;
    esac
    shift
done

# Function to log messages
log() {
    local message="[$(date '+%Y-%m-%d %H:%M:%S')] $1"
    echo "$message"
    echo "$message" >> "$LOG_FILE"
}

# Function to execute AWS CLI command with exponential backoff
aws_api_call() {
    local cmd="$1"
    local retry=0
    local wait_time=$DELAY
    local result
    local temp_output
    local final_output=""
    local last_result=""
    
    while true; do
        # Store output in a variable and capture exit status
        temp_output=$(mktemp)
        eval "$cmd" > "$temp_output" 2>&1
        local status=$?
        result=$(<"$temp_output")
        rm -f "$temp_output"
        
        if [ $status -eq 0 ]; then
            # Only return actual command output on success
            echo "$result"
            return 0
        elif [[ "$result" == *"Throttling"* || "$result" == *"ThrottlingException"* || "$result" == *"Rate exceeded"* ]]; then
            # Save the last result even if throttled (might contain partial data)
            if [[ -n "$result" && "$result" != *"ThrottlingException"* && "$result" != *"Throttling"* ]]; then
                last_result="$result"
            fi
            
            if [ $retry -lt $MAX_RETRIES ]; then
                # Send log messages to stderr to avoid contaminating command output
                log "API throttled, retrying in ${wait_time}s... ($((retry+1))/$MAX_RETRIES)" >&2
                log "$result" >&2
                sleep $wait_time
                retry=$((retry+1))
                wait_time=$((wait_time * 2)) # Exponential backoff
            else
                log "WARNING: Maximum retries exceeded due to throttling. Continuing with partial or empty results." >&2
                log "Command was: $cmd" >&2
                # Return the last result we got (might be empty or partial) and continue
                echo "$last_result"
                return 0
            fi
        else
            log "ERROR: Command failed: $cmd" >&2
            log "Error message: $result" >&2
            return $status
        fi
    done
}

# Function to safely extract and validate marker values
get_valid_marker() {
    local raw_marker="$1"
    
    # Clean up the marker value - remove newlines, trim spaces
    local clean_marker=$(echo "$raw_marker" | tr -d '\n\r' | xargs)
    
    # Check if marker is None, "None", empty or contains "None" repeated
    if [[ -z "$clean_marker" || "$clean_marker" == "None" || "$clean_marker" == *"None None"* || "$clean_marker" == *"NoneNone"* ]]; then
        log "Debug: Invalid marker detected: '$clean_marker', returning empty string"
        echo ""
        return 0
    fi
    
    # Return the valid marker
    echo "$clean_marker"
}

# Calculate cutoff date (current date minus DAYS)
CUTOFF_DATE=$(date -d "$DAYS days ago" '+%Y-%m-%d')
log "Starting AWS IAM cleanup for resources inactive since: $CUTOFF_DATE"
log "Dry run mode: $DRY_RUN"
log "Using delay of $DELAY seconds between API calls"
log "Using page size of $PAGE_SIZE for list operations"

# Check AWS CLI is installed and configured
if ! command -v aws &> /dev/null; then
    log "ERROR: AWS CLI is not installed. Please install it first."
    exit 1
fi

# Function to check and delete inactive IAM users
cleanup_iam_users() {
    log "Checking for inactive IAM users..."
    
    # Get all IAM users with pagination
    USERS=""
    MARKER=""
    PAGE_COUNT=1
    log "Retrieving IAM users with pagination (page size: $PAGE_SIZE)"
    
    while true; do
        MARKER_PARAM=""
        if [[ -n "$MARKER" ]]; then
            MARKER_PARAM="--marker \"$MARKER\""
        fi
        
        log "Fetching page $PAGE_COUNT of users..."
        PAGE_DATA=$(aws_api_call "aws iam list-users --max-items $PAGE_SIZE $MARKER_PARAM --query 'Users[*].[UserName,PasswordLastUsed]' --output text")
        STATUS=$?
        
        if [ $STATUS -ne 0 ] && [ -z "$PAGE_DATA" ]; then
            if [ $PAGE_COUNT -eq 1 ]; then
                log "ERROR: Failed to retrieve any IAM users"
                return 1
            else
                log "WARNING: Failed to retrieve page $PAGE_COUNT of users, continuing with data collected so far"
                break
            fi
        fi
        
        # Append the page data to our results
        USERS="${USERS}${PAGE_DATA}"$'\n'
        
        # Check if there are more pages, using our new marker validation function
        raw_marker=$(aws_api_call "aws iam list-users --max-items $PAGE_SIZE $MARKER_PARAM --query 'Marker' --output text")
        MARKER=$(get_valid_marker "$raw_marker")
        
        if [[ -z "$MARKER" ]]; then
            log "All user pages retrieved successfully (total pages: $PAGE_COUNT)"
            break
        fi
        
        log "Using marker: '$MARKER' for next page"
        PAGE_COUNT=$((PAGE_COUNT + 1))
        sleep $DELAY # Rate limiting between pagination calls
    done
    
    # Count total users for progress tracking
    total_users=$(echo "$USERS" | grep -v '^$' | wc -l)
    current_user=0
    inactive_count=0
    log "Found $total_users IAM users to check"
        
    while IFS=$'\t' read -r user last_used_date; do
        # Skip empty lines or lines that look like log entries
        if [[ -z "$user" || "$user" == *"["* ]]; then
            log "Skipping invalid user entry: $user"
            continue
        fi
        
        current_user=$((current_user + 1))
        log "Processing user $current_user/$total_users: $user"
        
        # Only process users with ci-op or ci-ln prefix
        if [[ "${user:0:5}" != "ci-op" && "${user:0:5}" != "ci-ln" ]]; then
            log "Skipping non-target user: $user (not targeted for cleanup)"
            continue
        fi
        
        sleep $DELAY # Rate limiting between user processing
        
        # If user has never used password, check access keys
        if [[ "$last_used_date" == "None" || -z "$last_used_date" ]]; then
            # Get access keys for user
            ACCESS_KEYS_OUTPUT=$(aws_api_call "aws iam list-access-keys --user-name \"$user\" --query 'AccessKeyMetadata[*].AccessKeyId' --output text")
            LAST_ACTIVITY="None"
            # For each access key, check last used
            if [ -n "$ACCESS_KEYS_OUTPUT" ]; then
                for key in $ACCESS_KEYS_OUTPUT; do
                    # Skip entries that look like log messages
                    if [[ "$key" == *"["* || -z "$key" ]]; then
                        continue
                    fi
                    
                    KEY_LAST_USED_OUTPUT=$(aws_api_call "aws iam get-access-key-last-used --access-key-id \"$key\" --query 'AccessKeyLastUsed.LastUsedDate' --output text")
                    
                    # Skip if output contains log timestamp or is empty
                    if [[ -z "$KEY_LAST_USED_OUTPUT" || "$KEY_LAST_USED_OUTPUT" == *"["* ]]; then
                        continue
                    fi
                    
                    KEY_LAST_USED="$KEY_LAST_USED_OUTPUT"
                    sleep $DELAY # Rate limiting between API calls
                    
                    if [[ "$KEY_LAST_USED" != "None" && ("$LAST_ACTIVITY" == "None" || "$KEY_LAST_USED" > "$LAST_ACTIVITY") ]]; then
                        LAST_ACTIVITY="$KEY_LAST_USED"
                    fi
                done
            fi
            
            last_used_date="$LAST_ACTIVITY"
        fi
        
        # Validate the date format before attempting comparison
        is_valid_date=true
        if [[ "$last_used_date" != "None" ]]; then
            formatted_date=$(date -d "$last_used_date" '+%Y-%m-%d' 2>/dev/null) || is_valid_date=false
        fi
        
        # If never used, last used before cutoff date, or starts with ci- prefix, delete
        if [[ "$last_used_date" == "None" || 
              ("$is_valid_date" == "true" && "$formatted_date" < "$CUTOFF_DATE") ]]; then
            
            inactive_count=$((inactive_count + 1))
            log "User $user is inactive (last activity: $last_used_date) [$inactive_count inactive found]"
            
            if [[ "$DRY_RUN" == "false" ]]; then
                # Delete access keys
                ACCESS_KEYS_LIST=$(aws_api_call "aws iam list-access-keys --user-name \"$user\" --query 'AccessKeyMetadata[*].AccessKeyId' --output text")
                # Validate the response doesn't contain error indicators
                if [[ -n "$ACCESS_KEYS_LIST" && "$ACCESS_KEYS_LIST" != *"ERROR"* ]]; then
                    for key in $ACCESS_KEYS_LIST; do
                        # Validate key format - AWS access keys are typically 20 characters
                        if [[ ${#key} -ge 16 && "$key" != *"["* ]]; then
                            log "Deleting access key $key for user $user"
                            aws_api_call "aws iam delete-access-key --user-name \"$user\" --access-key-id \"$key\""
                            sleep $DELAY # Rate limiting
                        fi
                    done
                fi
                
                # Remove user from groups
                GROUP_LIST=$(aws_api_call "aws iam list-groups-for-user --user-name \"$user\" --query 'Groups[*].GroupName' --output text")
                # Validate the response
                if [[ -n "$GROUP_LIST" && "$GROUP_LIST" != *"ERROR"* && "$GROUP_LIST" != *"["* ]]; then
                    for group in $GROUP_LIST; do
                        # Basic validation of group name
                        if [[ -n "$group" && "$group" != *"["* && "$group" != *"]"* && "$group" != "ERROR:"* ]]; then
                            log "Removing user $user from group $group"
                            aws_api_call "aws iam remove-user-from-group --user-name \"$user\" --group-name \"$group\""
                            sleep $DELAY # Rate limiting
                        else
                            log "Skipping invalid group name: $group"
                        fi
                    done
                fi
                
                # Delete inline policies
                POLICY_LIST=$(aws_api_call "aws iam list-user-policies --user-name \"$user\" --query 'PolicyNames[*]' --output text")
                # Validate the response
                if [[ -n "$POLICY_LIST" && "$POLICY_LIST" != *"ERROR"* && "$POLICY_LIST" != *"["* ]]; then
                    for policy in $POLICY_LIST; do
                        # Basic validation of policy name
                        if [[ -n "$policy" && "$policy" != *"["* && "$policy" != *"]"* && "$policy" != "ERROR:"* ]]; then
                            log "Deleting inline policy $policy from user $user"
                            aws_api_call "aws iam delete-user-policy --user-name \"$user\" --policy-name \"$policy\""
                            sleep $DELAY # Rate limiting
                        else
                            log "Skipping invalid policy name: $policy"
                        fi
                    done
                fi
                
                # Detach managed policies
                for policy in $(aws_api_call "aws iam list-attached-user-policies --user-name \"$user\" --query 'AttachedPolicies[*].PolicyArn' --output text"); do
                    log "Detaching managed policy $policy from user $user"
                    aws_api_call "aws iam detach-user-policy --user-name \"$user\" --policy-arn \"$policy\""
                    sleep $DELAY # Rate limiting
                done
                
                # Delete login profile if exists
                aws iam get-login-profile --user-name "$user" &>/dev/null
                if [ $? -eq 0 ]; then
                    log "Deleting login profile for user $user"
                    aws_api_call "aws iam delete-login-profile --user-name \"$user\""
                    sleep $DELAY # Rate limiting
                fi
                
                # Delete the user
                log "Deleting user $user"
                aws_api_call "aws iam delete-user --user-name \"$user\""
                sleep $DELAY # Rate limiting
            else
                log "[DRY RUN] Would delete user $user"
            fi
        else
            log "User $user is active (last activity: $last_used_date)"
        fi
    done <<< "$USERS"
    
    log "Completed processing $total_users users. Found $inactive_count inactive users."
}

# Function to check and delete inactive IAM roles
cleanup_iam_roles() {
    log "Checking for inactive IAM roles..."
    
    # Get all IAM roles with pagination
    ROLES=""
    MARKER=""
    PAGE_COUNT=1
    log "Retrieving IAM roles with pagination (page size: $PAGE_SIZE)"
    
    while true; do
        MARKER_PARAM=""
        if [[ -n "$MARKER" ]]; then
            MARKER_PARAM="--marker \"$MARKER\""
        fi
        
        log "Fetching page $PAGE_COUNT of roles..."
        PAGE_DATA=$(aws_api_call "aws iam list-roles --max-items $PAGE_SIZE $MARKER_PARAM --query 'Roles[*].[RoleName,RoleLastUsed.LastUsedDate]' --output text")
        STATUS=$?
        
        if [ $STATUS -ne 0 ] && [ -z "$PAGE_DATA" ]; then
            if [ $PAGE_COUNT -eq 1 ]; then
                log "ERROR: Failed to retrieve any IAM roles"
                return 1
            else
                log "WARNING: Failed to retrieve page $PAGE_COUNT of roles, continuing with data collected so far"
                break
            fi
        fi
        
        # Append the page data to our results
        ROLES="${ROLES}${PAGE_DATA}"$'\n'
        
        # Check if there are more pages, using our new marker validation function
        raw_marker=$(aws_api_call "aws iam list-roles --max-items $PAGE_SIZE $MARKER_PARAM --query 'Marker' --output text")
        MARKER=$(get_valid_marker "$raw_marker")
        
        if [[ -z "$MARKER" ]]; then
            log "All role pages retrieved successfully (total pages: $PAGE_COUNT)"
            break
        fi
        
        log "Using marker: '$MARKER' for next page"
        PAGE_COUNT=$((PAGE_COUNT + 1))
        sleep $DELAY # Rate limiting between pagination calls
    done
    
    # Count total roles for progress tracking
    total_roles=$(echo "$ROLES" | grep -v '^$' | wc -l)
    current_role=0
    inactive_count=0
    log "Found $total_roles IAM roles to check"
        
    while read -r role last_used_date; do
        current_role=$((current_role + 1))
        log "Processing role $current_role/$total_roles: $role"
        
        # Only process roles with ci-op or ci-ln prefix
        if [[ "${role:0:5}" != "ci-op" && "${role:0:5}" != "ci-ln" ]]; then
            log "Skipping non-target role: $role (not targeted for cleanup)"
            continue
        fi
        
        sleep $DELAY # Rate limiting between role processing
        
        # If never used or last used before cutoff date, delete
        if [[ "$last_used_date" == "None" || "$(date -d "$last_used_date" '+%Y-%m-%d' 2>/dev/null)" < "$CUTOFF_DATE" ]]; then
            inactive_count=$((inactive_count + 1))
            log "Role $role is inactive (last activity: $last_used_date) [$inactive_count inactive found]"
            
            if [[ "$DRY_RUN" == "false" ]]; then
                # First try to convert inline policies to managed policies when possible
                POLICY_LIST=$(aws_api_call "aws iam list-role-policies --role-name \"$role\" --query 'PolicyNames[*]' --output text")
                # Validate the response
                if [[ -n "$POLICY_LIST" && "$POLICY_LIST" != *"ERROR"* && "$POLICY_LIST" != *"["* ]]; then
                    for policy in $POLICY_LIST; do
                        # Basic validation of policy name
                        if [[ -n "$policy" && "$policy" != *"["* && "$policy" != *"]"* && "$policy" != "ERROR:"* ]]; then
                            # Instead of deleting, try to get and detach if possible
                            log "Trying to handle inline policy $policy for role $role"
                            
                            # Try to get policy document (we may not have permission for this)
                            POLICY_DOC=$(aws_api_call "aws iam get-role-policy --role-name \"$role\" --policy-name \"$policy\" --query 'PolicyDocument' --output json" 2>/dev/null)
                            if [[ -n "$POLICY_DOC" && "$POLICY_DOC" != *"ERROR"* ]]; then
                                log "Successfully retrieved policy document, will try detaching instead of deleting"
                            else
                                # If we can't get the policy, try the delete operation anyway
                                # The aws_api_call function will now handle permission errors gracefully
                                log "Attempting to delete inline policy $policy from role $role"
                                aws_api_call "aws iam delete-role-policy --role-name \"$role\" --policy-name \"$policy\""
                            fi
                            sleep $DELAY # Rate limiting
                        else
                            log "Skipping invalid policy name: $policy"
                        fi
                    done
                fi
                
                # Detach managed policies - this usually has better permissions
                ATTACHED_POLICIES=$(aws_api_call "aws iam list-attached-role-policies --role-name \"$role\" --query 'AttachedPolicies[*].PolicyArn' --output text")
                if [[ -n "$ATTACHED_POLICIES" && "$ATTACHED_POLICIES" != *"ERROR"* ]]; then
                    for policy in $ATTACHED_POLICIES; do
                        if [[ -n "$policy" && "$policy" != *"["* ]]; then
                            log "Detaching managed policy $policy from role $role"
                            aws_api_call "aws iam detach-role-policy --role-name \"$role\" --policy-arn \"$policy\""
                            sleep $DELAY # Rate limiting
                        fi
                    done
                fi
                
                # Try to delete the role, but don't fail if we can't
                log "Attempting to delete role $role"
                DELETE_RESULT=$(aws_api_call "aws iam delete-role --role-name \"$role\"")
                if [[ -z "$DELETE_RESULT" || "$DELETE_RESULT" == *"ERROR"* || "$DELETE_RESULT" == *"AccessDenied"* ]]; then
                    log "Unable to delete role $role completely. Manual cleanup may be required."
                else
                    log "Successfully deleted role $role"
                fi
            else
                log "[DRY RUN] Would delete role $role"
            fi
        else
            log "Role $role is active (last activity: $last_used_date)"
        fi
    done <<< "$ROLES"
    
    log "Completed processing $total_roles roles. Found $inactive_count inactive roles."
}

# Confirm deletion if not in dry run mode
if [[ "$DRY_RUN" == "false" ]]; then
    echo -n "WARNING: This will delete inactive IAM users and roles. Continue? (y/n): "
    read -r confirm
    if [[ "$confirm" != "y" && "$confirm" != "Y" ]]; then
        log "Operation cancelled by user"
        exit 0
    fi
fi

# Run the cleanup functions
cleanup_iam_users
cleanup_iam_roles

log "AWS IAM cleanup completed"

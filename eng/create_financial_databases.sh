#!/bin/bash

if [ "$#" -eq 0 ] || [ "$1" != "-y" ]; then
    
    printf "\n\nThis script will COMPLETELY REMOVE AND REPLACE your Kyna PostgreSQL databases.\n"
    printf "There is no recovery from this unless you have backups.\n\n"
    
    read -p "Are you sure you want to do this? (y/n): " confirmation
    
    if [ "$confirmation" != "y" ]; then
        printf "\nCrisis averted.\n"
        exit 0
    fi
fi

create_db(){
    local DB_NAME=$1
    local PG_HOST="localhost"
    local PG_USER="postgres"
    local PG_PORT="5432"
    
    printf "\nDROPPING $DB_NAME\n"
    dropdb -h $PG_HOST -p $PG_PORT -U $PG_USER $DB_NAME
    
    printf "\nCREATING $DB_NAME\n"
    createdb -h $PG_HOST -p $PG_PORT -U $PG_USER $DB_NAME
    
    printf "_______________________________________\n"
}

run_script(){
    local DB_NAME=$
    local SCRIPT_LOCATION=$2
    local PG_HOST="localhost"
    local PG_USER="postgres"
    local PG_PORT="5432"
    
    printf "\nRUNNING $SCRIPT_LOCATION AGAINST $DB_NAME\n"
    psql -h $PG_HOST -p $PG_PORT -U $PG_USER -d $DB_NAME -f $SCRIPT_LOCATION
    
    printf "_______________________________________\n"
}

FINANCIALS_DB=("financials_test" "financials")

for db_name in "${FINANCIALS_DB[@]}"; do
    create_db $db_name
    run_script $db_name "../database/postgresql/create_prices_tables.sql"
    run_script $db_name "../database/postgresql/create_fundamental_tables.sql"
done

printf "\nDatabase setups complete.\n"

exit 0
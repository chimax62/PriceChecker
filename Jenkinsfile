#!/usr/bin/env groovy

def dockerTag = cicd.dockerTag
env.dockertag_application = "application-$dockerTag"
env.dockertag_mongodb = "mongodb-$dockerTag"
env.NETWORK_NAME = "network-$dockerTag"
env.OCTOPUS_URL = "https://octopus3.yoox.net"
env.NUGET_FEED = "http://artifactory.yoox.net/artifactory/api/nuget/ynap-virtual-nuget"
env.NUGET_LIBRARY_FEED = "http://artifactory.yoox.net/artifactory/api/nuget/ynap-virtual-nuget-library"

node("lin && onprem") {
  timestamps {
    timeout(time: 15, unit: 'MINUTES') {
      ansiColor('xterm') {
        try {
          stage("Checkout scm") {
            scm_env = checkout scm

            env.BRANCH_NAME = scm_env.GIT_BRANCH
            env.COMMIT_SHA = sh(
               script: "git log -1 --pretty=format:%h",
               returnStdout: true
            )
            env.COMMIT_AUTHOR = sh(
               script: "git log -1 --pretty=format:%an",
               returnStdout: true
            )
            env.COMMIT_COMMENT = sh(
               script: "git log -1 --pretty=format:%B",
               returnStdout: true
            ).trim()
            env.COMMIT_DATE = sh(
               script: "git log -1 --pretty=%ad --date=format:%c",
               returnStdout: true
            )
            env.COMMIT_COUNT = sh(
              script: "git rev-list --all --count",
              returnStdout: true
            ).trim()
            def version = "1.0.0.${env.COMMIT_COUNT}"
            versionSuffix = ""
            if(env.BRANCH_NAME != "master")
              versionSuffix = "-" + env.BRANCH_NAME.replaceAll(/[^a-zA-Z0-9\-]/, "-")
            env.EXTENDED_VERSION = "${version}-H${env.COMMIT_SHA}${versionSuffix}"
            //Prevent errors while passing the commit comment as an env variable to Docker
            env.NORMALIZED_COMMIT_COMMENT = env.COMMIT_COMMENT.replaceAll("(?:\\n|\\r|\"|\')", "")
          }

          stage("Prepare environment") {
            currentBuild.displayName = env.EXTENDED_VERSION
            sh "docker-compose up --build -d"
            sh "docker cp . ${dockertag_application}:/work"
          }
          stage("Build") {
            sh "docker exec -t ${dockertag_application} pwsh ./Make.ps1 -command Build -productionEnv"
          }
          stage("Solution tests") {
            sh "docker exec -t ${dockertag_application} pwsh ./Make.ps1 -command Test -productionEnv"
          }

          if (env.BRANCH_NAME == "master" || env.BRANCH_NAME.startsWith("hotfix/") || env.BRANCH_NAME.startsWith("perf/") || env.BRANCH_NAME.startsWith("test/")) {
            withCredentials([
              usernamePassword(credentialsId: 'artifactory_yoox', usernameVariable: 'ARTIFACTORY_USERNAME', passwordVariable: 'ARTIFACTORY_PASSWORD'),
              usernamePassword(credentialsId: 'octopus', usernameVariable: 'notUsed', passwordVariable: 'OCTOPUS_APIKEY')
            ]) {
              stage("Create Package") {
                sh "docker exec -t ${dockertag_application} pwsh ./Make.ps1 -command Pack -productionEnv"
              }
              stage("Push Package") {
                sh "docker exec -t ${dockertag_application} pwsh ./Make.ps1 -command Push -productionEnv -artifactoryUsername ${ARTIFACTORY_USERNAME} -artifactoryPassword ${ARTIFACTORY_PASSWORD}"
              }
              stage("Create Release"){
                sh "docker exec -t ${dockertag_application} pwsh ./Make.ps1 -command Release -productionEnv -octopusApyKey ${OCTOPUS_APIKEY}"
              }
            }
          }
        } catch(err) {
          echo "Failed: see logs for details"
          throw(err)
        } finally {
          stage("Clean environment") {
            sh "docker-compose down --rmi local"
          }
        }
      }
    }
  }
}

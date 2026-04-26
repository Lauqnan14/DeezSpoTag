#define _GNU_SOURCE

#include <errno.h>
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <sys/mount.h>
#include <unistd.h>
#include <string.h>

#include "cmdline.h"

pid_t child_proc = -1;
struct gengetopt_args_info args_info;
#define CAP_SYS_ADMIN_IDX 21
#define CAP_SYS_ADMIN_BIT (1ULL << CAP_SYS_ADMIN_IDX)

static void intHan(int signum) {
    if (child_proc != -1) {
        kill(child_proc, SIGKILL);
    }
}

static int has_cap_sys_admin() {
    FILE *fp;
    char line[256];
    unsigned long long cap_eff = 0;
    int found_cap_eff = 0;

    fp = fopen("/proc/self/status", "r");
    if (fp == NULL) {
        return 0;
    }

    while (fgets(line, sizeof(line), fp) != NULL) {
        if (strncmp(line, "CapEff:", 7) == 0) {
            char *value_str = line + 7;
            while (*value_str == '\t' || *value_str == ' ') {
                value_str++;
            }
            cap_eff = strtoull(value_str, NULL, 16);
            found_cap_eff = 1;
            break;
        }
    }

    fclose(fp);

    if (!found_cap_eff) {
        return 0;
    }

    return (cap_eff & CAP_SYS_ADMIN_BIT) != 0;
}

int main(int argc, char *argv[], char *envp[]) {
    cmdline_parser(argc, argv, &args_info);
    if (signal(SIGINT, intHan) == SIG_ERR) {
        perror("signal");
        return 1;
    }

    mkdir("./rootfs/proc", 0755);
    if (mount("proc", "./rootfs/proc", "proc", 0, NULL) != 0) {
        if (errno == EPERM || errno == EACCES || errno == ENOSYS) {
            fprintf(stderr, "warning: mount(proc) unavailable (%s); continuing without proc mount\n", strerror(errno));
        } else {
            perror("mount proc");
            return 1;
        }
    }

    mkdir("./rootfs/dev", 0755);
    if (mount("/dev", "./rootfs/dev", NULL, MS_BIND, NULL) != 0) {
        if (errno == EPERM || errno == EACCES || errno == ENOSYS) {
            fprintf(stderr, "warning: mount(/dev bind) unavailable (%s); continuing with existing device nodes\n", strerror(errno));
        } else {
            perror("mount /dev");
            return 1;
        }
    }

    if (chdir("./rootfs") != 0) {
        perror("chdir");
        return 1;
    }
    if (chroot("./") != 0) {
        perror("chroot");
        return 1;
    }
    chmod("/system/bin/linker64", 0755);
    chmod("/system/bin/main", 0755);

    if (has_cap_sys_admin()) {
        if (unshare(CLONE_NEWPID)) {
            if (errno == EPERM || errno == EACCES || errno == ENOSYS) {
                fprintf(stderr, "warning: unshare(CLONE_NEWPID) unavailable (%s); continuing without PID namespace isolation\n", strerror(errno));
            } else {
                perror("unshare");
                return 1;
            }
        }
    }

    child_proc = fork();
    if (child_proc == -1) {
        perror("fork");
        return 1;
    }

    if (child_proc > 0) {
        wait(NULL);
        return 0;
    }

    // Child process logic
    mkdir(args_info.base_dir_arg, 0777);
    mkdir(strcat(args_info.base_dir_arg, "/mpl_db"), 0777);
    execve("/system/bin/main", argv, envp);
    perror("execve");
    return 1;
}

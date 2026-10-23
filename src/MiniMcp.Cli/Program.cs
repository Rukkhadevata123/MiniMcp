if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        MiniMcp

        用法：minimcp [--help]

        选项：
          -h, --help  显示帮助

        服务启动命令尚未接入。
        """);
    return 0;
}

Console.Error.WriteLine("未知命令或参数。请使用 --help 查看帮助。");
return 2;

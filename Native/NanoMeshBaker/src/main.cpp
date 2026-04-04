#include "NanoMeshBakeBackend.h"

#include <exception>
#include <iostream>
#include <string>

namespace
{
    struct CliOptions
    {
        std::string requestPath;
        std::string responsePath;
    };

    bool parseArguments(int argc, char** argv, CliOptions& outOptions, std::string& outError)
    {
        for (int i = 1; i < argc; ++i)
        {
            const std::string argument = argv[i];
            if (argument == "--request" && i + 1 < argc)
            {
                outOptions.requestPath = argv[++i];
            }
            else if (argument == "--response" && i + 1 < argc)
            {
                outOptions.responsePath = argv[++i];
            }
            else if (argument == "--help" || argument == "-h")
            {
                return true;
            }
            else
            {
                outError = "Unknown or incomplete argument: " + argument;
                return false;
            }
        }

        if (outOptions.requestPath.empty() || outOptions.responsePath.empty())
        {
            outError = "Usage: NanoMeshBakerCli --request <request.bin> --response <response.bin>";
            return false;
        }

        return true;
    }
}

int main(int argc, char** argv)
{
    CliOptions options;
    std::string error;
    if (!parseArguments(argc, argv, options, error))
    {
        std::cerr << error << '\n';
        return 1;
    }

    if (options.requestPath.empty() && options.responsePath.empty())
    {
        std::cout << "NanoMeshBakerCli --request <request.bin> --response <response.bin>\n";
        return 0;
    }

    try
    {
        nanomesh::BakeRequest request;
        if (!nanomesh::readBakeRequest(options.requestPath, request, error))
        {
            std::cerr << error << '\n';
            return 2;
        }

        nanomesh::BakeResponse response;
        if (!nanomesh::runBake(request, response, error))
        {
            std::cerr << error << '\n';
            return 3;
        }

        if (!nanomesh::writeBakeResponse(options.responsePath, response, error))
        {
            std::cerr << error << '\n';
            return 4;
        }

        if (!response.success)
        {
            std::cerr << response.message << '\n';
            return 5;
        }

        std::cout << response.message << '\n';
        return 0;
    }
    catch (const std::exception& ex)
    {
        std::cerr << "NanoMesh bake failed with exception: " << ex.what() << '\n';
        return 10;
    }
}

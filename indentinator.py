import os
import sys

def count_tabs(line):
    """Counts the number of leading tab characters in a line."""
    count = 0
    for char in line:
        if char == '\t':
            count += 1
        else:
            break
    return count

def scan_cs_files(root_dir):
    """Scans all .cs files in the subtree for sudden indentation jumps."""
    if not os.path.isdir(root_dir):
        print(f"Error: '{root_dir}' is not a valid directory.")
        return

    print(f"Scanning '.cs' files in: {os.path.abspath(root_dir)}\n")
    match_count = 0

    for dirpath, _, filenames in os.walk(root_dir):
        for filename in filenames:
            if filename.endswith('.cs'):
                file_path = os.path.join(dirpath, filename)
                
                try:
                    with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                        prev_tabs = 0
                        
                        for line_num, line in enumerate(f, 1):
                            # Skip empty or whitespace-only lines
                            if not line.strip():
                                continue
                                
                            current_tabs = count_tabs(line)
                            
                            # Check if indentation increased by 2 or more tabs
                            if current_tabs >= prev_tabs + 2:
                                print(f"{file_path}:{line_num}")
                                print(f"  Prev tabs: {prev_tabs} | Current tabs: {current_tabs}")
                                print(f"  Line: {line.strip()}\n")
                                match_count += 1
                                
                            prev_tabs = current_tabs
                            
                except Exception as e:
                    print(f"Could not read file {file_path}: {e}")

    print(f"Scan complete. Found {match_count} instance(s).")

if __name__ == "__main__":
    # Use current directory if no path is provided via command line
    target_directory = sys.argv[1] if len(sys.argv) > 1 else "."
    scan_cs_files(target_directory)
